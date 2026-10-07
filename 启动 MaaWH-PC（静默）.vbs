Set fso = CreateObject("Scripting.FileSystemObject")

Set sh = CreateObject("Wscript.Shell")

dir = fso.GetParentFolderName(WScript.ScriptFullName)

sh.CurrentDirectory = dir

sh.Run "powershell -NoProfile -ExecutionPolicy Bypass -File " & Chr(34) & dir & "\scripts\sync_interface.ps1" & Chr(34), 0, True

sh.Environment("PROCESS")("DOTNET_ROOT") = dir & "\dotnet"

sh.Run Chr(34) & dir & "\MaaWH.exe" & Chr(34), 0, False

