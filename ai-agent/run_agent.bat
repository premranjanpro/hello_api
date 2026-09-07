@echo off
echo ========================================================
echo Starting AI Voice Agent (LiveKit + Groq + Edge-TTS)
echo ========================================================

cd /d "%~dp0"

if not exist venv (
    echo Creating Python virtual environment...
    python -m venv venv
)

echo Activating virtual environment...
call venv\Scripts\activate

echo Installing / Checking dependencies...
pip install -r requirements.txt

if not exist .env (
    echo Creating .env from .env.example...
    copy .env.example .env
    echo Please make sure your GROQ_API_KEY is configured in ai-agent\.env!
)

echo Starting LiveKit Voice Agent Worker...
python agent.py dev
pause
