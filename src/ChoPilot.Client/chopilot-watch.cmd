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

echo Cho-Pilot 자동 관측 — %CHOPILOT_WATCH_SECONDS%초 간격. 중단하려면 Ctrl+C.
echo.

"%~dp0chopilot-dump.exe" --watch %CHOPILOT_WATCH_SECONDS% --upload --delay 5

echo.
echo 종료되었습니다. 창을 닫으려면 아무 키나 누르세요.
pause > nul
endlocal
