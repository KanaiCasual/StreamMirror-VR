@echo off
rem Builds the quad views layer's offline test (v2\bin\quad-views-test\qvf_test.exe, Visual Studio 2022) and runs its
rem cases against the built layer. Build-Dev.ps1 runs it after building the layer.
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find VC\Auxiliary\Build\vcvars64.bat`) do set VCVARS=%%i
if "%VCVARS%"=="" (echo Visual Studio with C++ was not found & exit /b 1)
call "%VCVARS%" >nul 2>&1
set OUT=%~dp0..\..\bin\quad-views-test
if not exist "%OUT%" mkdir "%OUT%"
cd /d "%OUT%"
set DEPS=%~dp0..\..\external\qvf-deps\openxr-sdk
cl /nologo /std:c++17 /EHsc /O2 /W3 /MD /I "%DEPS%\include" /I "%DEPS%\src\common" /I "%~dp0..\layer" "%~dp0qvf_test.cpp" /Fe:qvf_test.exe /link d3d11.lib d3d12.lib dxgi.lib dxguid.lib windowscodecs.lib ole32.lib
if errorlevel 1 exit /b 1
del qvf_test.obj 2>nul
set LAYER=%~dp0..\..\bin\quad-views\XR_APILAYER_NOVENDOR_gaze_mirror_quad_views.dll
set FAILED=0
for %%c in (gaze-filter d3d11 d3d12 d3d12-array d3d11-and-d3d12 vulkan-declared official-below) do (
    "%OUT%\qvf_test.exe" "%LAYER%" "%OUT%\out" %%c
    if errorlevel 1 set FAILED=1
)
exit /b %FAILED%
