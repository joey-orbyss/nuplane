#include <stddef.h>
#include <stdio.h>
#include <sys/stat.h>

#if defined(__x86_64__)
#define NUPLANE_ARCHITECTURE "x64"
#elif defined(__arm64__) || defined(__aarch64__)
#define NUPLANE_ARCHITECTURE "arm64"
#else
#error "The Darwin stat ABI canary supports only x64 and arm64."
#endif

int main(void)
{
    printf(
        "{\"Architecture\":\"%s\",\"Size\":%zu,\"Device\":%zu,\"Mode\":%zu,\"LinkCount\":%zu,\"Inode\":%zu,\"Length\":%zu}\n",
        NUPLANE_ARCHITECTURE,
        sizeof(struct stat),
        offsetof(struct stat, st_dev),
        offsetof(struct stat, st_mode),
        offsetof(struct stat, st_nlink),
        offsetof(struct stat, st_ino),
        offsetof(struct stat, st_size));
    return 0;
}
