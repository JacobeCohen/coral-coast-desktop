' Starts Coral Launcher without a console window.
'   CoralLauncher.vbs        start (or restart) the launcher
'   CoralLauncher.vbs show   what the pinned taskbar button runs: bring the running launcher
'                            forward with the search box ready, or start it if it isn't running
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
showIt = False
If WScript.Arguments.Count > 0 Then showIt = (LCase(WScript.Arguments(0)) = "show")

If showIt Then
  ' port from config.json ("wallpaperPort"), default 47613
  port = "47613"
  On Error Resume Next
  If fso.FileExists(dir & "\config.json") Then
    txt = fso.OpenTextFile(dir & "\config.json", 1).ReadAll()
    Set re = New RegExp
    re.Pattern = """wallpaperPort""\s*:\s*(\d+)"
    Set m = re.Execute(txt)
    If m.Count > 0 Then port = m(0).SubMatches(0)
  End If
  Err.Clear
  Set http = CreateObject("MSXML2.ServerXMLHTTP.6.0")
  http.setTimeouts 500, 500, 1000, 1000
  http.Open "POST", "http://127.0.0.1:" & port & "/show", False
  http.Send ""
  If Err.Number = 0 Then
    If http.Status = 200 Then WScript.Quit
  End If
  On Error GoTo 0
End If

args = ""
If showIt Then args = " -Show"
sh.Run "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\CoralLauncher.ps1""" & args, 0, False
