@echo off
REM ─────────────────────────────────────────────────────────────────────────
REM  원클릭 자동 관측 — 이 파일을 더블클릭하면 된다.
REM
REM  10초마다 포그라운드 창을 관측해 서버로 보낸다. 화면이 그대로면 보내지 않는다.
REM  창을 닫거나 Ctrl+C 를 누르면 멈춘다.
REM
REM  서버 주소는 appsettings.json 의 Server:IngestionEndpoint 를 쓴다.
REM  간격을 바꾸려면 아래 CHOPILOT_WATCH_SECONDS 를 고쳐라.
REM
REM  ※ 저장 버튼을 누른 순간의 화면(작업 완료 신호)은 이 반복으로 남길 수 없다.
REM     그건 chopilot-dump --completed --upload 로 따로 한 번 실행해야 한다.
REM     반복 캡처를 전부 완료로 기록하면 필수 필드 규칙의 증거가 오염되기 때문이다.
REM ─────────────────────────────────────────────────────────────────────────

setlocal
if "%CHOPILOT_WATCH_SECONDS%"=="" set CHOPILOT_WATCH_SECONDS=10

REM exe가 옆에 없으면 cmd는 "명령을 찾을 수 없습니다"만 뱉는다 — 폴더째 옮기지 않은 것이다.
if not exist "%~dp0chopilot-dump.exe" (
    echo [오류] chopilot-dump.exe 가 이 폴더에 없습니다.
    echo        chopilot-watch.cmd 는 같은 폴더의 exe를 부릅니다 - 폴더째 복사해야 합니다.
    echo        RUNBOOK 5장 5번 항목을 보세요.
    goto :done
)

echo Cho-Pilot 자동 관측 — %CHOPILOT_WATCH_SECONDS%초 간격. 중단하려면 Ctrl+C.
echo.

"%~dp0chopilot-dump.exe" --watch %CHOPILOT_WATCH_SECONDS% --upload --delay 5
set RC=%ERRORLEVEL%

REM 0=정상 종료, 1=프로그램이 스스로 낸 오류(그 경우 위에 사유가 찍혀 있다).
REM 그 밖의 코드는 .NET 호스트가 런타임을 찾지 못한 것이다 — apphost의 hostfxr 오류는
REM 원인을 말해 주지 않으므로 여기서 짚어 준다.
if "%RC%"=="0" goto :done
if "%RC%"=="1" goto :done

REM echo 문구에 괄호를 넣지 마라 — 닫는 괄호가 if 블록을 그 자리에서 끝낸다.
echo.
echo [진단] .NET 런타임을 찾지 못했을 수 있습니다. 종료 코드 %RC%
if not exist "%~dp0hostfxr.dll" (
    echo        이 폴더는 프레임워크 의존 배포라 시스템에 설치된 .NET이 필요합니다.
    echo.
    echo        1. dotnet --info 의 Base Path 를 확인하고 그 상위 폴더로:
    echo             setx DOTNET_ROOT "C:\경로\dotnet"
    echo           그런 다음 *새 창* 에서 다시 실행하세요.
    echo.
    echo        2. 근본 해결 - 런타임을 폴더에 포함해 배포:
    echo             dotnet publish src\ChoPilot.Client -c Release -r win-x64 --self-contained -o C:\ChoPilot
) else (
    echo        self-contained 배포인데 실행에 실패했습니다 - 폴더 일부만 복사되었을 수 있습니다.
)

:done
echo.
echo 종료되었습니다. 창을 닫으려면 아무 키나 누르세요.
pause > nul
endlocal
