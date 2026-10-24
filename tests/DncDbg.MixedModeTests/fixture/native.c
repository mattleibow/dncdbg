#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#define NOINLINE __declspec(noinline)
#else
#define EXPORT __attribute__((visibility("default")))
#define NOINLINE __attribute__((noinline))
#endif

static NOINLINE int native_sum(int left, int right)
{
    int sum = left + right; /* NATIVE_BREAK */
    return sum;
}

EXPORT NOINLINE int native_add(int left, int right)
{
    int result = native_sum(left, right);
    return result;
}
