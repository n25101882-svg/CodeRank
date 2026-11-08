#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/capability.h>
#include <linux/magic.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/statvfs.h>
#include <sys/syscall.h>
#include <sys/utsname.h>
#include <sys/wait.h>
#include <unistd.h>

enum { STATUS_FD = 3, SETUP_ERROR = 125 };
static const char nsjail[] = "/opt/coderank-judge-sandbox/bin/nsjail";

static bool write_all(int fd, const void *data, size_t length)
{
    const char *bytes = data;
    while (length != 0) {
        ssize_t written = write(fd, bytes, length);
        if (written < 0 && errno == EINTR)
            continue;
        if (written <= 0)
            return false;
        bytes += written;
        length -= (size_t)written;
    }
    return true;
}

static int failure(int fd, char stage, int error)
{
    char marker[8];
    int length = snprintf(marker, sizeof(marker), "F%c:%d", stage,
        error > 0 && error <= 4095 ? error : 0);
    if (length > 0 && (size_t)length < sizeof(marker))
        (void)write_all(fd, marker, (size_t)length);
    return SETUP_ERROR;
}

static int launch(int argc, char **argv)
{
    if (argc < 6 || strcmp(argv[4], nsjail) != 0 || getuid() == 0 || getuid() != geteuid())
        return SETUP_ERROR;
    int receipt = open(argv[3], O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (receipt < 0)
        return SETUP_ERROR;
    if (receipt != STATUS_FD && dup2(receipt, STATUS_FD) < 0)
        return failure(receipt, 'L', errno);
    if (fcntl(STATUS_FD, F_SETFD, 0) != 0 ||
        syscall(SYS_close_range, (unsigned int)STATUS_FD + 1, UINT_MAX, 0U) != 0)
        return failure(STATUS_FD, 'L', errno);

    int group = open(argv[2], O_WRONLY | O_CLOEXEC | O_NOFOLLOW);
    if (group < 0)
        return failure(STATUS_FD, 'C', errno);
    ssize_t joined;
    do {
        joined = write(group, "0\n", 2);
    } while (joined < 0 && errno == EINTR);
    if (joined != 2)
        return failure(STATUS_FD, 'C', joined < 0 ? errno : EIO);
    if (close(group) != 0)
        return failure(STATUS_FD, 'C', errno);

    execv(nsjail, &argv[4]);
    return failure(STATUS_FD, 'L', errno);
}

static bool absent(const char *path)
{
    struct stat info;
    return lstat(path, &info) < 0 && errno == ENOENT;
}

static int attest(const char *payload, bool executable, unsigned long temp_bytes)
{
    struct __user_cap_header_struct header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    struct __user_cap_data_struct caps[2] = { 0 };
    struct utsname uts;
    struct statvfs root, data, temporary;
    struct statfs fs;
    const unsigned long readonly = ST_RDONLY | ST_NOSUID | ST_NODEV;
    const unsigned long temp_flags = ST_NOSUID | ST_NODEV | ST_NOEXEC;
    if (getpid() != 1 || getuid() != 65534 || geteuid() != 65534 ||
        getgid() != 65534 || getegid() != 65534)
        return failure(STATUS_FD, 'A', 0);
    if (uname(&uts) != 0)
        return failure(STATUS_FD, 'A', errno);
    if (strcmp(uts.nodename, "coderank-jail") != 0)
        return failure(STATUS_FD, 'A', 0);
    int no_new_privs = prctl(PR_GET_NO_NEW_PRIVS, 0L, 0L, 0L, 0L);
    if (no_new_privs != 1)
        return failure(STATUS_FD, 'A', no_new_privs < 0 ? errno : 0);
    if (syscall(SYS_capget, &header, caps) != 0)
        return failure(STATUS_FD, 'A', errno);
    for (size_t i = 0; i < 2; ++i)
        if (caps[i].effective || caps[i].permitted || caps[i].inheritable)
            return failure(STATUS_FD, 'A', 0);

    int seccomp = prctl(PR_GET_SECCOMP, 0L, 0L, 0L, 0L);
    if (seccomp != 2)
        return failure(STATUS_FD, 'S', seccomp < 0 ? errno : 0);
    long denied = syscall(SYS_unshare, 0);
    if (denied != -1 || errno != ENOMSG)
        return failure(STATUS_FD, 'S', denied == -1 ? errno : 0);
    denied = syscall(SYS_socket, AF_INET, SOCK_STREAM, 0);
    if (denied != -1 || errno != EPERM)
        return failure(STATUS_FD, 'S', denied == -1 ? errno : 0);

    if (!absent("/proc/self") || !absent("/sys/fs/cgroup"))
        return failure(STATUS_FD, 'M', 0);
    if (statvfs("/", &root) != 0 || statvfs(payload, &data) != 0 ||
        statfs("/tmp", &fs) != 0 || statvfs("/tmp", &temporary) != 0)
        return failure(STATUS_FD, 'M', errno);
    if ((root.f_flag & readonly) != readonly || (data.f_flag & readonly) != readonly ||
        ((data.f_flag & ST_NOEXEC) == 0) != executable || fs.f_type != TMPFS_MAGIC ||
        (temporary.f_flag & temp_flags) != temp_flags ||
        (temporary.f_flag & ST_RDONLY) != 0 || temporary.f_frsize == 0 ||
        temporary.f_blocks == 0 || temporary.f_blocks > temp_bytes / temporary.f_frsize ||
        temporary.f_files == 0 || temporary.f_files > 4096)
        return failure(STATUS_FD, 'M', 0);
    return 0;
}

static int compile(void)
{
    pid_t compiler = fork();
    if (compiler < 0)
        return failure(STATUS_FD, 'E', errno);
    if (compiler == 0) {
        if (dup2(STDERR_FILENO, STDOUT_FILENO) < 0)
            _exit(failure(STATUS_FD, 'E', errno));
        char *const args[] = { "/usr/bin/g++", "-std=c++17", "-O2", "-pipe", "-static", "-s",
            "-fdiagnostics-color=never", "-o", "/tmp/program", "/job/Main.cpp", NULL };
        execv(args[0], args);
        _exit(failure(STATUS_FD, 'E', errno));
    }
    int status;
    pid_t waited;
    do {
        waited = waitpid(compiler, &status, 0);
    } while (waited < 0 && errno == EINTR);
    if (waited != compiler)
        return failure(STATUS_FD, 'E', waited < 0 ? errno : 0);
    if (!WIFEXITED(status))
        return WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 1;
    if (WEXITSTATUS(status) != 0)
        return WEXITSTATUS(status);

    int artifact = open("/tmp/program", O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    struct stat info;
    if (artifact < 0 || fstat(artifact, &info) != 0)
        return failure(STATUS_FD, 'E', errno);
    if (!S_ISREG(info.st_mode) || info.st_size == 0)
        return failure(STATUS_FD, 'E', 0);
    char buffer[8192];
    for (;;) {
        ssize_t count = read(artifact, buffer, sizeof(buffer));
        if (count < 0 && errno == EINTR)
            continue;
        if (count < 0)
            return failure(STATUS_FD, 'E', errno);
        if (count == 0)
            return close(artifact) == 0 ? 0 : failure(STATUS_FD, 'E', errno);
        if (!write_all(STDOUT_FILENO, buffer, (size_t)count))
            return 1;
    }
}

int main(int argc, char **argv)
{
    if (argc >= 2 && strcmp(argv[1], "launch") == 0)
        return launch(argc, argv);
    if (argc != 3)
        return failure(STATUS_FD, 'A', 0);
    if (fcntl(STATUS_FD, F_SETFD, FD_CLOEXEC) != 0)
        return failure(STATUS_FD, 'A', errno);
    char *end;
    errno = 0;
    unsigned long temp_bytes = strtoul(argv[2], &end, 10);
    if (errno != 0 || *end != '\0' || temp_bytes < 1048576 || temp_bytes > 33554432)
        return failure(STATUS_FD, 'A', errno);

    const bool compiling = strcmp(argv[1], "compile") == 0;
    const bool cpp = strcmp(argv[1], "cpp17") == 0;
    if (!compiling && !cpp && strcmp(argv[1], "python3") != 0)
        return failure(STATUS_FD, 'A', 0);
    const char *payload = compiling ? "/job/Main.cpp" : cpp ? "/job/program" : "/job/Main.py";
    int attestation = attest(payload, cpp, temp_bytes);
    if (attestation != 0)
        return attestation;
    if (prctl(PR_SET_DUMPABLE, 0L, 0L, 0L, 0L) != 0)
        return failure(STATUS_FD, 'A', errno);
    if (!write_all(STATUS_FD, "R", 1))
        return SETUP_ERROR;
    if (compiling)
        return compile();
    if (cpp) {
        char *const args[] = { "/job/program", NULL };
        execv(args[0], args);
    } else {
        char *const args[] = { "/usr/bin/python3", "-I", "-B", "-S", "-u", "/job/Main.py", NULL };
        execv(args[0], args);
    }
    return failure(STATUS_FD, 'E', errno);
}
