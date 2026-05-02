@echo off
REM ============================================================
REM  glTF Mesh Separator - one-click launcher
REM  ----------------------------------------------------------
REM  Drag a .vrm or .glb file onto this .bat to split each
REM  multi-submesh mesh into independent meshes.
REM  Output: <input>_separated.<ext>  (next to the input file)
REM
REM  Without arguments: shows usage and runs --analyze on the
REM  first .vrm/.glb found next to this script.
REM ============================================================

setlocal
cd /d "%~dp0"

if "%~1"=="" (
    echo No input file given.
    echo.
    echo  Drop a .vrm or .glb file onto this .bat,
    echo  or run from a terminal:
    echo      glTF_mesh_separator.bat ^<input.vrm^> [output.vrm ^| --analyze]
    echo.
    pause
    goto :eof
)

python "%~dp0glTF_mesh_separator.py" "%~1" %2 %3
echo.
pause
endlocal
