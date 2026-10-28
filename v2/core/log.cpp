#include "pch.h"

#include "log.h"

namespace gaze_mirror {

    namespace {
        std::mutex g_lock;
        FILE* g_file = nullptr;
        bool g_tried = false;
        std::wstring g_name = L"streammirror";

        void Open() {
            g_tried = true;
            wchar_t overridden[MAX_PATH];
            const DWORD overriddenLength = GetEnvironmentVariableW(L"STREAMMIRROR_LOG_FILE", overridden, MAX_PATH);
            if (overriddenLength > 0 && overriddenLength < MAX_PATH) {
                g_file = _wfsopen(overridden, L"w", _SH_DENYWR);
                return;
            }
            std::wstring path = DataFolder();
            if (path.empty()) return;
            path += L"\\" + g_name + L".log";
            MoveFileExW(path.c_str(), (path + L".previous").c_str(), MOVEFILE_REPLACE_EXISTING);
            g_file = _wfsopen(path.c_str(), L"w", _SH_DENYWR);
        }

        void Write(const char* format, va_list arguments) {
            std::lock_guard<std::mutex> lock(g_lock);
            if (!g_tried) Open();
            if (!g_file) return;
            SYSTEMTIME now;
            GetLocalTime(&now);
            fprintf(g_file, "%02d:%02d:%02d.%03d  ", now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
            vfprintf(g_file, format, arguments);
            fputc('\n', g_file);
            fflush(g_file);
        }
        // Copies what `to` lacks from `from`, a level of folders deep (the quad views layer's folder is one).
        void CopyMissing(const std::wstring& from, const std::wstring& to) {
            CreateDirectoryW(to.c_str(), nullptr);
            WIN32_FIND_DATAW found;
            HANDLE search = FindFirstFileW((from + L"\\*").c_str(), &found);
            if (search == INVALID_HANDLE_VALUE) return;
            do {
                const std::wstring name = found.cFileName;
                if (name == L"." || name == L"..") continue;
                if (found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) CopyMissing(from + L"\\" + name, to + L"\\" + name);
                else CopyFileW((from + L"\\" + name).c_str(), (to + L"\\" + name).c_str(), TRUE);
            } while (FindNextFileW(search, &found));
            FindClose(search);
        }
    } // namespace

    std::wstring DataFolder() {
        wchar_t local[MAX_PATH];
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
        if (length == 0 || length >= MAX_PATH) return {};
        const std::wstring folder = std::wstring(local) + L"\\StreamMirror";
        const std::wstring previous = std::wstring(local) + L"\\GazeMirror"; // LEGACY-NAME: the folder before 3.0
        const auto exists = [](const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; };
        // Copied, never moved: the app tidies the old folder away once it runs as 3.0 (a test run of these files must not
        // take it from an older install).
        if (!exists(folder) && exists(previous)) CopyMissing(previous, folder);
        CreateDirectoryW(folder.c_str(), nullptr);
        return folder;
    }

    void SetLogName(const wchar_t* name) {
        std::lock_guard<std::mutex> lock(g_lock);
        if (!g_tried) g_name = name;
    }

    void Log(const char* format, ...) {
        va_list arguments;
        va_start(arguments, format);
        Write(format, arguments);
        va_end(arguments);
    }

    void LogFewTimes(int& counter, int limit, const char* format, ...) {
        if (counter >= limit) return;
        counter++;
        va_list arguments;
        va_start(arguments, format);
        Write(format, arguments);
        va_end(arguments);
    }

} // namespace gaze_mirror
