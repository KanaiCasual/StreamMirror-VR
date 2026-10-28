// StreamMirror VR - core: a plain text log. Each program says where its log goes (SetLogFile) before the
// first line; until then, lines go to %LocalAppData%\StreamMirror\<name>.log with the name given here.
#pragma once

#include <cstdarg>
#include <string>

namespace gaze_mirror {

    // Chooses the file. `name` is used as %LocalAppData%\StreamMirror\<name>.log; the environment variable
    // STREAMMIRROR_LOG_FILE (tests) wins over it. The previous log of the same name is kept once as ".previous".
    void SetLogName(const wchar_t* name);

    // %LocalAppData%\StreamMirror, made if missing. Before 3.0 the folder was %LocalAppData%\GazeMirror: the first of
    // our programs to start after the update copies it over (the app removes the old one later).
    std::wstring DataFolder();

    // printf-style; a time stamp is added.
    void Log(const char* format, ...);

    // For things that would otherwise be written every frame: only the first `limit` times.
    void LogFewTimes(int& counter, int limit, const char* format, ...);

} // namespace gaze_mirror
