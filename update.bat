@echo off
armada server stop
call reinstall-tool.bat
armada server start
