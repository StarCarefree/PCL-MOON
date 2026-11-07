/* MMCSS Games registration adapted from MinecraftMMCSS (MIT), Copyright (c) 2026 girlbossdev.
 * 通过 JNI 在实际 LWJGL 呈现线程调用，不依赖 JNA 或 Fabric。
 */
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#include <windows.h>
#include <jni.h>

typedef HANDLE (WINAPI *SetCharacteristics)(LPCWSTR, LPDWORD);
typedef BOOL (WINAPI *RevertCharacteristics)(HANDLE);
typedef BOOL (WINAPI *QueryResponsiveness)(HANDLE, PULONG);
static INIT_ONCE init_once = INIT_ONCE_STATIC_INIT;
static DWORD slot = FLS_OUT_OF_INDEXES;
static SetCharacteristics set_characteristics;
static RevertCharacteristics revert_characteristics;
static QueryResponsiveness query_responsiveness;
static volatile LONG64 thread_exit_releases = 0;

static VOID CALLBACK release_thread(PVOID handle) {
    /* FLS 回调在退出线程执行，满足 avrt 必须由注册线程注销的要求。 */
    if (handle && revert_characteristics && revert_characteristics(handle))
        InterlockedIncrement64(&thread_exit_releases);
}

static BOOL CALLBACK initialize(PINIT_ONCE once, PVOID parameter, PVOID *context) {
    (void)once; (void)parameter; (void)context;
    /* 系统 DLL 只从 System32 加载，保持模块存活以供 FLS 回调使用。 */
    HMODULE avrt = LoadLibraryExW(L"avrt.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!avrt) return TRUE;
    set_characteristics = (SetCharacteristics)GetProcAddress(avrt, "AvSetMmThreadCharacteristicsW");
    revert_characteristics = (RevertCharacteristics)GetProcAddress(avrt, "AvRevertMmThreadCharacteristics");
    query_responsiveness = (QueryResponsiveness)GetProcAddress(avrt, "AvQuerySystemResponsiveness");
    if (set_characteristics && revert_characteristics && query_responsiveness)
        slot = FlsAlloc(release_thread);
    return TRUE; /* 服务或 API 缺失时保守停用，不在每帧重试。 */
}

JNIEXPORT jboolean JNICALL Java_pclmoon_startup_Mmcss_acquire0(JNIEnv *env, jclass type) {
    (void)env; (void)type;
    if (!InitOnceExecuteOnce(&init_once, initialize, NULL, NULL) || slot == FLS_OUT_OF_INDEXES) return JNI_FALSE;
    if (FlsGetValue(slot)) return JNI_TRUE;
    DWORD task_index = 0;
    HANDLE handle = set_characteristics(L"Games", &task_index);
    if (!handle) return JNI_FALSE;
    if (!FlsSetValue(slot, handle)) {
        revert_characteristics(handle);
        return JNI_FALSE;
    }
    return JNI_TRUE;
}

JNIEXPORT jboolean JNICALL Java_pclmoon_startup_Mmcss_registered0(JNIEnv *env, jclass type) {
    (void)env; (void)type;
    if (slot == FLS_OUT_OF_INDEXES) return JNI_FALSE;
    HANDLE handle = FlsGetValue(slot);
    ULONG responsiveness = 0;
    /* 直接向 Windows 验证句柄状态，不把本地标记当作注册成功。 */
    return handle && query_responsiveness(handle, &responsiveness) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jlong JNICALL Java_pclmoon_startup_Mmcss_threadExitReleases0(JNIEnv *env, jclass type) {
    (void)env; (void)type;
    return (jlong)InterlockedCompareExchange64(&thread_exit_releases, 0, 0);
}

JNIEXPORT jboolean JNICALL Java_pclmoon_startup_Mmcss_release0(JNIEnv *env, jclass type) {
    (void)env; (void)type;
    if (slot == FLS_OUT_OF_INDEXES) return JNI_TRUE;
    HANDLE handle = FlsGetValue(slot);
    if (!handle) return JNI_TRUE;
    if (!revert_characteristics(handle)) return JNI_FALSE;
    FlsSetValue(slot, NULL);
    return JNI_TRUE;
}
