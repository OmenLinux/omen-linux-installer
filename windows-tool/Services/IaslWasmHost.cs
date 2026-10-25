using System.IO;
using System.Text;
using Wasmtime;

namespace OmenPrep.Services;

/// <summary>
/// Host for the open-source ACPICA <c>iasl.wasm</c>
/// (https://github.com/alessandromrc/iasl-wasm) via Wasmtime.
/// Implements the Emscripten <c>env</c> + WASI imports that build expects.
/// </summary>
internal sealed class IaslWasmHost : IDisposable
{
    // Emscripten / WASI errno (same numbering)
    private const int EBADF = 8;
    private const int ENOENT = 44;
    private const int EACCES = 2;
    private const int EINVAL = 28;

    private readonly string _workDir;
    private readonly List<IaslLogLine> _logs = [];
    private readonly Dictionary<int, Fd> _fds = new();
    private int _nextFd = 3;
    private int _exitCode;
    private bool _exited;

    private Engine? _engine;
    private Store? _store;
    private Instance? _instance;
    private Memory? _memory;

    private sealed class Fd
    {
        public required System.IO.Stream Handle;
        public required string Path;
        public bool IsWritable;
        public int Flags;
    }

    public IReadOnlyList<IaslLogLine> Logs => _logs;
    public int ExitCode => _exitCode;

    public IaslWasmHost(string workDir)
    {
        _workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(_workDir);
        _fds[0] = new Fd { Handle = System.IO.Stream.Null, Path = "<stdin>", IsWritable = false };
        _fds[1] = new Fd { Handle = new MemoryStream(), Path = "<stdout>", IsWritable = true };
        _fds[2] = new Fd { Handle = new MemoryStream(), Path = "<stderr>", IsWritable = true };
    }

    public void Run(byte[] wasmBytes, string[] argv)
    {
        _engine = new Engine();
        using var module = Module.FromBytes(_engine, "iasl", wasmBytes);
        _store = new Store(_engine);
        var linker = new Linker(_engine);

        DefineWasi(linker);
        DefineEmscriptenEnv(linker);

        _instance = linker.Instantiate(_store, module);
        _memory = _instance.GetMemory("memory")
            ?? throw new InvalidOperationException("iasl.wasm has no exported memory.");

        _instance.GetAction("emscripten_stack_init")?.Invoke();
        _instance.GetAction("__wasm_call_ctors")?.Invoke();

        var main = _instance.GetFunction("__main_argc_argv")
            ?? throw new InvalidOperationException("iasl.wasm missing __main_argc_argv.");

        var (argc, argvPtr) = WriteArgv(argv);
        try
        {
            var result = main.Invoke(argc, argvPtr);
            if (!_exited && result is int code)
                _exitCode = code;
        }
        catch (Exception) when (_exited)
        {
            // proc_exit / abort after setting _exitCode
        }
        catch (TrapException ex)
        {
            _logs.Add(new IaslLogLine("stderr", ex.Message));
            if (!_exited) _exitCode = 1;
        }

        FlushStdStreams();
    }

    private void DefineWasi(Linker linker)
    {
        linker.DefineFunction("wasi_snapshot_preview1", "proc_exit", (int code) =>
        {
            _exitCode = code;
            _exited = true;
            throw new TrapException("proc_exit");
        });

        linker.DefineFunction("wasi_snapshot_preview1", "fd_write",
            (Caller caller, int fd, int iovs, int iovsLen, int nwrittenPtr) =>
            {
                var mem = Mem(caller);
                if (!_fds.TryGetValue(fd, out var entry) || !entry.IsWritable)
                    return EBADF;
                var written = 0;
                for (var i = 0; i < iovsLen; i++)
                {
                    var basePtr = mem.ReadInt32(iovs + i * 8);
                    var len = mem.ReadInt32(iovs + i * 8 + 4);
                    if (len <= 0) continue;
                    var span = mem.GetSpan(basePtr, len);
                    entry.Handle.Write(span);
                    if (fd is 1 or 2)
                        _logs.Add(new IaslLogLine(fd == 1 ? "stdout" : "stderr", Encoding.UTF8.GetString(span)));
                    written += len;
                }
                mem.WriteInt32(nwrittenPtr, written);
                return 0;
            });

        linker.DefineFunction("wasi_snapshot_preview1", "fd_read",
            (Caller caller, int fd, int iovs, int iovsLen, int nreadPtr) =>
            {
                var mem = Mem(caller);
                if (!_fds.TryGetValue(fd, out var entry))
                    return EBADF;
                var readTotal = 0;
                for (var i = 0; i < iovsLen; i++)
                {
                    var basePtr = mem.ReadInt32(iovs + i * 8);
                    var len = mem.ReadInt32(iovs + i * 8 + 4);
                    if (len <= 0) continue;
                    var buf = new byte[len];
                    var n = entry.Handle.Read(buf, 0, len);
                    if (n > 0)
                        buf.AsSpan(0, n).CopyTo(mem.GetSpan(basePtr, n));
                    readTotal += n;
                    if (n < len) break;
                }
                mem.WriteInt32(nreadPtr, readTotal);
                return 0;
            });

        linker.DefineFunction("wasi_snapshot_preview1", "fd_close", (int fd) =>
        {
            if (fd < 3) return 0;
            if (!_fds.Remove(fd, out var entry)) return EBADF;
            entry.Handle.Dispose();
            return 0;
        });

        linker.DefineFunction("wasi_snapshot_preview1", "fd_seek",
            (Caller caller, int fd, long offset, int whence, int newOffsetPtr) =>
            {
                var mem = Mem(caller);
                if (!_fds.TryGetValue(fd, out var entry)) return EBADF;
                var origin = whence switch
                {
                    0 => SeekOrigin.Begin,
                    1 => SeekOrigin.Current,
                    2 => SeekOrigin.End,
                    _ => SeekOrigin.Begin,
                };
                try
                {
                    var pos = entry.Handle.Seek(offset, origin);
                    mem.WriteInt64(newOffsetPtr, pos);
                    return 0;
                }
                catch
                {
                    return EINVAL;
                }
            });

        linker.DefineFunction("wasi_snapshot_preview1", "fd_fdstat_get",
            (Caller caller, int fd, int statPtr) =>
            {
                var mem = Mem(caller);
                if (!_fds.ContainsKey(fd)) return EBADF;
                var span = mem.GetSpan(statPtr, 24);
                span.Clear();
                span[0] = 4; // filetype regular_file
                return 0;
            });

        linker.DefineFunction("wasi_snapshot_preview1", "clock_time_get",
            (Caller caller, int clockId, long precision, int timePtr) =>
            {
                var mem = Mem(caller);
                var ns = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds * 1_000_000L;
                mem.WriteInt64(timePtr, ns);
                return 0;
            });
    }

    private void DefineEmscriptenEnv(Linker linker)
    {
        linker.DefineFunction("env", "exit", (int code) =>
        {
            _exitCode = code;
            _exited = true;
            throw new TrapException("exit");
        });

        linker.DefineFunction("env", "emscripten_date_now",
            () => (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        linker.DefineFunction("env", "emscripten_get_now",
            () => (double)Environment.TickCount64);

        linker.DefineFunction("env", "emscripten_resize_heap", (Caller caller, int requested) =>
        {
            var mem = Mem(caller);
            var pages = (requested + 65535) / 65536;
            var cur = (int)mem.GetSize();
            if (pages <= cur) return 1;
            try
            {
                mem.Grow(pages - cur);
                return 1;
            }
            catch
            {
                return 0;
            }
        });

        linker.DefineFunction("env", "_abort_js", () =>
        {
            _exitCode = 1;
            _exited = true;
            throw new TrapException("abort");
        });

        linker.DefineFunction("env", "_tzset_js",
            (int timezone, int daylight, int stdName, int dstName) => { /* no-op */ });

        linker.DefineFunction("env", "_localtime_js",
            (Caller caller, long t, int tmPtr) =>
            {
                var mem = Mem(caller);
                var dt = DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().DateTime;
                WriteTm(mem, tmPtr, dt);
            });

        // Exact signatures from iasl.wasm import section
        linker.DefineFunction("env", "__syscall_openat",
            (Caller caller, int dirfd, int pathPtr, int flags, int varargs) =>
                SysOpenAt(caller, pathPtr, flags, varargs));

        linker.DefineFunction("env", "__syscall_fcntl64",
            (Caller caller, int fd, int cmd, int varargs) => SysFcntl(caller, fd, cmd, varargs));

        linker.DefineFunction("env", "__syscall_ioctl",
            (Caller caller, int fd, int op, int varargs) => 0);

        linker.DefineFunction("env", "__syscall_dup3",
            (int fd, int newfd, int flags) => -EINVAL);

        linker.DefineFunction("env", "__syscall_unlinkat",
            (Caller caller, int dirfd, int pathPtr, int flags) =>
            {
                var path = ReadCString(Mem(caller), pathPtr);
                try
                {
                    var full = MapPath(path);
                    if (flags == 512) Directory.Delete(full);
                    else File.Delete(full);
                    return 0;
                }
                catch { return -ENOENT; }
            });

        linker.DefineFunction("env", "__syscall_rmdir",
            (Caller caller, int pathPtr) =>
            {
                try
                {
                    Directory.Delete(MapPath(ReadCString(Mem(caller), pathPtr)));
                    return 0;
                }
                catch { return -ENOENT; }
            });

        linker.DefineFunction("env", "__syscall_fstat64",
            (Caller caller, int fd, int buf) =>
            {
                if (!_fds.TryGetValue(fd, out var entry)) return -EBADF;
                return WriteStat(Mem(caller), buf, entry.Path);
            });

        linker.DefineFunction("env", "__syscall_stat64",
            (Caller caller, int pathPtr, int buf) =>
                WriteStat(Mem(caller), buf, MapPath(ReadCString(Mem(caller), pathPtr))));

        linker.DefineFunction("env", "__syscall_lstat64",
            (Caller caller, int pathPtr, int buf) =>
                WriteStat(Mem(caller), buf, MapPath(ReadCString(Mem(caller), pathPtr))));

        linker.DefineFunction("env", "__syscall_newfstatat",
            (Caller caller, int dirfd, int pathPtr, int buf, int flags) =>
                WriteStat(Mem(caller), buf, MapPath(ReadCString(Mem(caller), pathPtr))));
    }

    private int SysOpenAt(Caller caller, int pathPtr, int flags, int varargs)
    {
        var mem = Mem(caller);
        var path = ReadCString(mem, pathPtr);
        var full = MapPath(path);

        const int O_ACCMODE = 3;
        const int O_WRONLY = 1;
        const int O_RDWR = 2;
        const int O_CREAT = 64;
        const int O_TRUNC = 512;
        const int O_DIRECTORY = 65536;

        try
        {
            if ((flags & O_DIRECTORY) != 0)
            {
                if (!Directory.Exists(full)) return -ENOENT;
                var dfd = _nextFd++;
                _fds[dfd] = new Fd { Handle = System.IO.Stream.Null, Path = full, IsWritable = false, Flags = flags };
                return dfd;
            }

            FileMode mode = FileMode.Open;
            FileAccess access = FileAccess.Read;
            var acc = flags & O_ACCMODE;
            if (acc == O_WRONLY) access = FileAccess.Write;
            else if (acc == O_RDWR) access = FileAccess.ReadWrite;

            if ((flags & O_CREAT) != 0)
                mode = (flags & O_TRUNC) != 0 ? FileMode.Create : FileMode.OpenOrCreate;
            else if ((flags & O_TRUNC) != 0)
                mode = FileMode.Truncate;

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var stream = new FileStream(full, mode, access, FileShare.ReadWrite);
            var fd = _nextFd++;
            _fds[fd] = new Fd
            {
                Handle = stream,
                Path = full,
                IsWritable = access != FileAccess.Read,
                Flags = flags,
            };
            return fd;
        }
        catch (FileNotFoundException) { return -ENOENT; }
        catch (DirectoryNotFoundException) { return -ENOENT; }
        catch (Exception ex)
        {
            _logs.Add(new IaslLogLine("stderr", $"openat {path}: {ex.Message}"));
            return -EACCES;
        }
    }

    private int SysFcntl(Caller caller, int fd, int cmd, int varargs)
    {
        if (!_fds.TryGetValue(fd, out var entry)) return -EBADF;
        return cmd switch
        {
            1 or 2 => 0,          // F_GETFD / F_SETFD
            3 => entry.Flags,     // F_GETFL
            4 => 0,               // F_SETFL (ignore)
            12 => 0,              // F_GETLK — pretend unlocked
            13 or 14 => 0,        // F_SETLK*
            _ => -EINVAL,
        };
    }

    private static int WriteStat(Memory mem, int buf, string path)
    {
        try
        {
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return -ENOENT;

            long size = 0;
            DateTime atime = DateTime.UnixEpoch, mtime = DateTime.UnixEpoch, ctime = DateTime.UnixEpoch;
            if (isDir)
            {
                var di = new DirectoryInfo(path);
                atime = di.LastAccessTimeUtc;
                mtime = di.LastWriteTimeUtc;
                ctime = di.CreationTimeUtc;
            }
            else
            {
                var fi = new FileInfo(path);
                size = fi.Length;
                atime = fi.LastAccessTimeUtc;
                mtime = fi.LastWriteTimeUtc;
                ctime = fi.CreationTimeUtc;
            }

            // Emscripten wasm32 stat layout (see iasl.js SYSCALLS.writeStat)
            var mode = isDir ? 0x41ED : 0x81A4;
            mem.WriteInt32(buf + 0, 1);          // st_dev
            mem.WriteInt32(buf + 4, mode);       // st_mode
            mem.WriteInt32(buf + 8, 1);          // st_nlink
            mem.WriteInt32(buf + 12, 0);         // uid
            mem.WriteInt32(buf + 16, 0);         // gid
            mem.WriteInt32(buf + 20, 0);         // rdev
            mem.WriteInt64(buf + 24, size);      // st_size
            mem.WriteInt32(buf + 32, 4096);      // st_blksize
            mem.WriteInt32(buf + 36, (int)((size + 511) / 512));
            WriteTime(mem, buf + 40, atime);
            WriteTime(mem, buf + 56, mtime);
            WriteTime(mem, buf + 72, ctime);
            mem.WriteInt64(buf + 88, 1);         // st_ino
            return 0;
        }
        catch { return -ENOENT; }
    }

    private static void WriteTime(Memory mem, int offset, DateTime utc)
    {
        var ms = (utc - DateTime.UnixEpoch).TotalMilliseconds;
        var sec = (long)Math.Floor(ms / 1000);
        var nsec = (int)((ms % 1000) * 1_000_000);
        mem.WriteInt64(offset, sec);
        mem.WriteInt32(offset + 8, nsec);
    }

    private string MapPath(string wasmPath)
    {
        if (string.IsNullOrEmpty(wasmPath) || wasmPath == ".")
            return _workDir;

        var p = wasmPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(p) || p.StartsWith(Path.DirectorySeparatorChar))
        {
            p = p.TrimStart(Path.DirectorySeparatorChar);
            if (p.StartsWith("work" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                p = p[("work".Length + 1)..];
        }

        var full = Path.GetFullPath(Path.Combine(_workDir, p));
        if (!full.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(wasmPath);
        return full;
    }

    private (int argc, int argvPtr) WriteArgv(string[] argv)
    {
        var mem = _memory!;
        var stackAlloc = _instance!.GetFunction("_emscripten_stack_alloc")
            ?? throw new InvalidOperationException("missing _emscripten_stack_alloc");

        int Alloc(int n)
        {
            var r = stackAlloc.Invoke(n);
            return r is int i ? i : throw new InvalidOperationException("stack_alloc failed");
        }

        var ptrs = new int[argv.Length];
        for (var i = 0; i < argv.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(argv[i] + "\0");
            var p = Alloc(bytes.Length);
            bytes.CopyTo(mem.GetSpan(p, bytes.Length));
            ptrs[i] = p;
        }

        var argvPtr = Alloc((argv.Length + 1) * 4);
        for (var i = 0; i < argv.Length; i++)
            mem.WriteInt32(argvPtr + i * 4, ptrs[i]);
        mem.WriteInt32(argvPtr + argv.Length * 4, 0);
        return (argv.Length, argvPtr);
    }

    private void FlushStdStreams()
    {
        // Already logged line-by-line in fd_write; nothing extra needed.
    }

    private Memory Mem(Caller caller) =>
        caller.GetMemory("memory") ?? _memory ?? throw new InvalidOperationException("no memory");

    private static string ReadCString(Memory mem, int ptr)
    {
        if (ptr == 0) return "";
        return mem.ReadNullTerminatedString(ptr);
    }

    private static void WriteTm(Memory mem, int tmPtr, DateTime dt)
    {
        mem.WriteInt32(tmPtr + 0, dt.Second);
        mem.WriteInt32(tmPtr + 4, dt.Minute);
        mem.WriteInt32(tmPtr + 8, dt.Hour);
        mem.WriteInt32(tmPtr + 12, dt.Day);
        mem.WriteInt32(tmPtr + 16, dt.Month - 1);
        mem.WriteInt32(tmPtr + 20, dt.Year - 1900);
        mem.WriteInt32(tmPtr + 24, (int)dt.DayOfWeek);
        mem.WriteInt32(tmPtr + 28, dt.DayOfYear - 1);
        mem.WriteInt32(tmPtr + 32, 0);
    }

    public void Dispose()
    {
        foreach (var fd in _fds.Values)
            fd.Handle.Dispose();
        _fds.Clear();
        _store?.Dispose();
        _engine?.Dispose();
    }
}
