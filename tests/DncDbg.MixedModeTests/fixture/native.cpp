#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#define NOINLINE __declspec(noinline)
#else
#define EXPORT __attribute__((visibility("default")))
#define NOINLINE __attribute__((noinline))
#endif

using managed_callback = int (*)(int value);

extern "C" int native_add(int left, int right);

static NOINLINE int native_after_callback(int sum, int callback_result)
{
    int result = sum + callback_result; /* NATIVE_AFTER_CALLBACK */
    return result;
}

extern "C" EXPORT NOINLINE int native_callback_roundtrip(
    int left,
    int right,
    managed_callback callback)
{
    int sum = native_add(left, right); /* NATIVE_BEFORE_CALLBACK */
    int callback_result = callback(sum);
    return native_after_callback(sum, callback_result);
}
