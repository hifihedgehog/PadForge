// Stands in for OpenRGB's LogManager.h in the KeyboardLayoutManager harness.
// The log calls still evaluate their arguments, as OpenRGB's LogAppend does,
// so a key list read out of bounds in a log line fails the checked build.
#pragma once
#include <string>
template<class... A> inline void harness_log(A&&...) {}
#define LOG_FATAL(...)   harness_log(__VA_ARGS__)
#define LOG_ERROR(...)   harness_log(__VA_ARGS__)
#define LOG_WARNING(...) harness_log(__VA_ARGS__)
#define LOG_INFO(...)    harness_log(__VA_ARGS__)
#define LOG_VERBOSE(...) harness_log(__VA_ARGS__)
#define LOG_DEBUG(...)   harness_log(__VA_ARGS__)
#define LOG_TRACE(...)   harness_log(__VA_ARGS__)
#define LOG_DIALOG(...)  harness_log(__VA_ARGS__)
