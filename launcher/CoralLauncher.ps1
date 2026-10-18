<#
  Coral Launcher - a Coral Coast app launcher that lives on the desktop (center screen).
  - Category tiles pop up a grid of the shortcuts in  <launchRoot>\<Category>\
  - Drives tile shows each drive with a free-space bar
  - Pinned favourites on the bar, a search box that searches every category
  - a hotkey (Ctrl+Shift+Space by default) brings it to the front from anywhere
  Settings: config.json next to this script.   Errors: CoralLauncher.log next to this script.
  Start it with CoralLauncher.vbs (no console window).
#>
param([switch]$Debug, [switch]$Show)

$ErrorActionPreference = 'Stop'
$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogFile = Join-Path $Root 'CoralLauncher.log'

# keep the log small: when it passes 1 MB, keep only the newest 200 KB
try {
    if ((Test-Path -LiteralPath $LogFile) -and (Get-Item -LiteralPath $LogFile).Length -gt 1MB) {
        $tail = [System.IO.File]::ReadAllText($LogFile)
        [System.IO.File]::WriteAllText($LogFile, $tail.Substring($tail.Length - 200KB))
    }
} catch {}

function Write-Log([string]$msg) {
    try { Add-Content -LiteralPath $LogFile -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $msg) -Encoding UTF8 } catch {}
}

# calendarIcs can be one address or a list of them: "calendarIcs": ["https://...", "https://..."]
function Get-CalendarUrls($c) {
    [string[]]@(@($c.calendarIcs) | Where-Object { $_ -and ([string]$_).Trim() } | ForEach-Object { [string]$_ })
}

# ---- one copy only: ask any running copy to close, then take over ----
$quit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'Local\CoralLauncher.Quit')
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($false, 'Local\CoralLauncher.v2', [ref]$createdNew)
[void]$quit.Set()
$got = $false
try { $got = $mutex.WaitOne(5000) } catch [System.Threading.AbandonedMutexException] { $got = $true }
[void]$quit.Reset()
if (-not $got) { Write-Log 'another copy did not close - exiting'; exit }

try {
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml, System.Drawing

    $refs = @(
        [System.Windows.Window].Assembly.Location,                 # PresentationFramework
        [System.Windows.Media.Imaging.BitmapSource].Assembly.Location, # PresentationCore
        [System.Windows.Int32Rect].Assembly.Location,              # WindowsBase
        [System.Xaml.XamlReader].Assembly.Location,                # System.Xaml
        [System.Drawing.Bitmap].Assembly.Location                  # System.Drawing (photo thumbnails)
    )
    if (-not ('CoralNative' -as [type])) {
        Add-Type -Path (Join-Path $Root 'CoralNative.cs') -ReferencedAssemblies $refs
    }

    # ---------------- settings ----------------
    # first run: start from the example settings
    $CfgPath = Join-Path $Root 'config.json'
    if (-not (Test-Path -LiteralPath $CfgPath) -and (Test-Path -LiteralPath (Join-Path $Root 'config.example.json'))) {
        Copy-Item -LiteralPath (Join-Path $Root 'config.example.json') -Destination $CfgPath
        Write-Log 'created config.json from config.example.json'
    }
    # a typo in config.json shouldn't stop the launcher: fall back to the last settings that worked
    $GoodCfg = Join-Path $Root 'config.lastgood.json'
    try {
        $cfg = Get-Content -LiteralPath $CfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Copy-Item -LiteralPath $CfgPath -Destination $GoodCfg -Force
    } catch {
        if (-not (Test-Path -LiteralPath $GoodCfg)) { throw }
        Write-Log "config.json has a mistake ($($_.Exception.Message)) - using the last settings that worked until it's fixed"
        $cfg = Get-Content -LiteralPath $GoodCfg -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    # launchRoot may use variables like %USERPROFILE%; empty means an "apps" folder next to this script
    $LaunchRoot = if ($cfg.launchRoot) { [Environment]::ExpandEnvironmentVariables([string]$cfg.launchRoot).TrimEnd('\') } else { Join-Path $Root 'apps' }
    if (-not (Test-Path -LiteralPath $LaunchRoot)) {
        try {
            foreach ($c in @($cfg.categoryOrder)) { if ($c) { New-Item -ItemType Directory -Force -Path (Join-Path $LaunchRoot $c) | Out-Null } }
            New-Item -ItemType Directory -Force -Path $LaunchRoot | Out-Null
            Write-Log "created launcher folders in $LaunchRoot"
        } catch { Write-Log "could not create $LaunchRoot : $_" }
    }
    $Layer = if ($cfg.layer) { [string]$cfg.layer } else { 'desktop' }   # desktop | normal | topmost

    # ---------------- start with Windows (HKCU Run entry; switch off in Task Manager > Startup or config) ----------------
    $RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    try {
        if ($cfg.startWithWindows -ne $false) {
            $runCmd = 'wscript.exe "' + (Join-Path $Root 'CoralLauncher.vbs') + '"'
            $current = $null
            $item = Get-ItemProperty -Path $RunKey -Name 'CoralLauncher' -ErrorAction SilentlyContinue
            if ($item) { $current = $item.CoralLauncher }
            if ($current -ne $runCmd) { Set-ItemProperty -Path $RunKey -Name 'CoralLauncher' -Value $runCmd; Write-Log 'added Coral Launcher to Windows startup' }
        }
        elseif (Get-ItemProperty -Path $RunKey -Name 'CoralLauncher' -ErrorAction SilentlyContinue) {
            Remove-ItemProperty -Path $RunKey -Name 'CoralLauncher'; Write-Log 'removed Coral Launcher from Windows startup'
        }
    } catch { Write-Log "startup entry: $_" }

    # ---------------- UI ----------------
    [xml]$xaml = Get-Content -LiteralPath (Join-Path $Root 'CoralLauncher.xaml') -Raw -Encoding UTF8
    $win = [System.Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))

    $ui = @{}
    foreach ($n in 'Drawer','DrawerTitle','DrawerCount','DrawerPath','CloseBtn','Items','Scroller','Bar','Tiles','Pins','Search','SearchHint') {
        $ui[$n] = $win.FindName($n)
    }
    $fontUri = New-Object System.Uri(([System.IO.Path]::Combine($Root, 'fonts')) + '\')
    $script:ScriptFont = New-Object System.Windows.Media.FontFamily($fontUri, './#Yellowtail')
    $ui.DrawerTitle.FontFamily = $script:ScriptFont

    $bc = New-Object System.Windows.Media.BrushConverter
    function Brush([string]$hex) { $b = $bc.ConvertFromString($hex); $b.Freeze(); return $b }
    $B = @{
        Ink      = Brush '#3B1F24'; InkSoft = Brush '#6B4A44'; Teal = Brush '#1D7364'; Cream = Brush '#FFF4EA'
        Wine     = Brush '#7A2630'; Coral   = Brush '#C24A36'
        ItemBg   = Brush '#8CFFF8F0'; ItemLine = Brush '#1F3B1F24'; Track = Brush '#1F3B1F24'
    }

    # ---------------- data ----------------
    $script:IconCache = @{}
    function Get-ItemIcon([string]$path) {
        if ($script:IconCache.ContainsKey($path)) { return $script:IconCache[$path] }
        $img = $null
        try { $img = [CoralNative]::GetIcon($path) } catch { Write-Log "icon (shell) failed for ${path}: $_" }
        if ($null -eq $img) {
            try {
                $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($path)
                $img = [System.Windows.Interop.Imaging]::CreateBitmapSourceFromHIcon($ico.Handle, [System.Windows.Int32Rect]::Empty, [System.Windows.Media.Imaging.BitmapSizeOptions]::FromEmptyOptions())
                $img.Freeze()
            } catch { }
        }
        $script:IconCache[$path] = $img
        return $img
    }

    $ShortcutExt = @('.lnk', '.url', '.exe', '.appref-ms', '.bat', '.cmd')

    function Get-Categories {
        if (-not (Test-Path -LiteralPath $LaunchRoot)) { Write-Log "launchRoot not found: $LaunchRoot"; return @() }
        $dirs  = @(Get-ChildItem -LiteralPath $LaunchRoot -Directory | Where-Object { -not $_.Name.StartsWith('_') })
        $order = @($cfg.categoryOrder)
        $dirs | Sort-Object @{ Expression = { $i = [array]::IndexOf($order, $_.Name); if ($i -lt 0) { 999 } else { $i } } }, Name
    }

    function Get-CategoryItems([string]$dir) {
        $cat = Split-Path -Leaf $dir
        @(Get-ChildItem -LiteralPath $dir -File |
            Where-Object { $ShortcutExt -contains $_.Extension.ToLower() } |
            Sort-Object BaseName |
            ForEach-Object { [pscustomobject]@{ Name = $_.BaseName; Path = $_.FullName; Sub = $cat } })
    }

    function Get-AllItems {
        $all = @()
        foreach ($d in (Get-Categories)) { $all += Get-CategoryItems $d.FullName }
        $all
    }

    # ---------------- actions ----------------
    function Start-Item([string]$path) {
        try {
            if ($path.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
                Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path)
            } else {
                Start-Process -FilePath $path
            }
        } catch { Write-Log "launch failed for ${path}: $_" }
        $ui.Search.Text = ''
        Close-Drawer
    }

    $script:Mode = $null        # category name, 'Drives', 'Search' or $null when closed
    $script:TileButtons = @{}

    function Set-ActiveTile([string]$key) {
        foreach ($k in @($script:TileButtons.Keys)) {
            $btn = $script:TileButtons[$k]
            if ($k -eq $key) { $btn.Style = $win.Resources['TileOn'] } else { $btn.Style = $win.Resources['Tile'] }
        }
    }

    function Close-Drawer {
        $script:Mode = $null
        $ui.Drawer.Visibility = 'Collapsed'
        Set-ActiveTile ''
    }

    function New-Text([string]$text, [double]$size, $brush, [string]$weight = 'Normal') {
        $t = New-Object System.Windows.Controls.TextBlock
        $t.Text = $text; $t.FontSize = $size; $t.Foreground = $brush
        $t.FontWeight = [System.Windows.FontWeights]::$weight
        $t.TextTrimming = 'CharacterEllipsis'
        return $t
    }

    function New-ItemButton($item, [double]$usedPct = -1) {
        $btn = New-Object System.Windows.Controls.Button
        $btn.Style = $win.Resources['Item']
        $btn.Tag = $item.Path
        $btn.ToolTip = $item.Name

        $grid = New-Object System.Windows.Controls.Grid
        $c0 = New-Object System.Windows.Controls.ColumnDefinition; $c0.Width = New-Object System.Windows.GridLength(48)
        $c1 = New-Object System.Windows.Controls.ColumnDefinition
        [void]$grid.ColumnDefinitions.Add($c0); [void]$grid.ColumnDefinitions.Add($c1)

        $img = New-Object System.Windows.Controls.Image
        $img.Width = 36; $img.Height = 36; $img.HorizontalAlignment = 'Left'
        $img.Source = Get-ItemIcon $item.Path
        [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($img, 'HighQuality')
        [void]$grid.Children.Add($img)

        $stack = New-Object System.Windows.Controls.StackPanel
        $stack.VerticalAlignment = 'Center'
        [System.Windows.Controls.Grid]::SetColumn($stack, 1)
        [void]$stack.Children.Add((New-Text $item.Name 14 $B.Ink 'SemiBold'))
        if ($usedPct -ge 0) {
            $track = New-Object System.Windows.Controls.Border
            $track.Height = 6; $track.CornerRadius = 3; $track.Background = $B.Track; $track.Margin = '0,5,0,4'
            $fill = New-Object System.Windows.Controls.Border
            $fill.HorizontalAlignment = 'Left'; $fill.CornerRadius = 3
            $fill.Width = [math]::Max(4, 150 * $usedPct / 100)
            if ($usedPct -ge 85) { $fill.Background = $B.Coral } else { $fill.Background = $B.Teal }
            $track.Child = $fill
            [void]$stack.Children.Add($track)
        }
        [void]$stack.Children.Add((New-Text $item.Sub 12 $B.InkSoft))
        [void]$grid.Children.Add($stack)

        $btn.Content = $grid
        $btn.Add_Click({ param($s, $e) Start-Item ([string]$s.Tag) })
        return $btn
    }

    function Show-Drawer([string]$title, [string]$count, [string]$pathText, $items, [switch]$Drives) {
        $ui.Items.Children.Clear()
        $ui.DrawerTitle.Text = $title
        $ui.DrawerCount.Text = $count
        $ui.DrawerPath.Text  = $pathText
        foreach ($it in $items) {
            if ($it.Header) {
                # full-width section heading inside the grid (forces a new row)
                $h = New-Text $it.Header 13 $B.InkSoft 'SemiBold'
                $h.Width = 960; $h.Margin = '2,4,0,8'
                [void]$ui.Items.Children.Add($h)
            }
            elseif ($Drives) { [void]$ui.Items.Children.Add((New-ItemButton $it $it.Used)) }
            else             { [void]$ui.Items.Children.Add((New-ItemButton $it)) }
        }
        if (@($items).Count -eq 0) {
            if ($script:Mode -eq 'Search') { $empty = 'No matches yet.' } else { $empty = 'Nothing here yet - drop shortcuts into the folder.' }
            [void]$ui.Items.Children.Add((New-Text $empty 14 $B.InkSoft))
        }
        $ui.Scroller.ScrollToTop()
        $ui.Drawer.Visibility = 'Visible'
    }

    function Open-Category([string]$name) {
        $dir = Join-Path $LaunchRoot $name
        $items = Get-CategoryItems $dir
        $script:Mode = $name
        Set-ActiveTile $name
        Show-Drawer $name ('{0} shortcuts' -f @($items).Count) ($dir + '\') $items
    }

    function Open-Drives {
        $items = foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
            if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
            $total = $d.TotalSize / 1GB; $used = ($d.TotalSize - $d.AvailableFreeSpace) / 1GB
            $label = if ($d.VolumeLabel) { $d.VolumeLabel } else { 'Local Disk' }
            [pscustomobject]@{
                Name = ('{0}  {1}' -f $d.Name.TrimEnd('\'), $label)
                Path = $d.RootDirectory.FullName
                Sub  = ('{0:N0} of {1:N0} GB used' -f $used, $total)
                Used = [math]::Round(100 * $used / [math]::Max($total, 1))
            }
        }
        $script:Mode = 'Drives'
        Set-ActiveTile 'Drives'
        Show-Drawer 'Drives' ('{0} drives' -f @($items).Count) 'click a drive to open it' $items -Drives
    }

    function Toggle([string]$key) {
        if ($script:Mode -eq $key) { Close-Drawer; return }
        $ui.Search.Text = ''
        if ($key -eq 'Drives') { Open-Drives } else { Open-Category $key }
    }

    # ---------------- search: launcher folders + Everything ----------------
    $UseEverything = ($cfg.everything -ne $false)
    $FileResults   = ($cfg.fileResults -ne $false)
    $script:Search = @{ Q = ''; Local = @(); Apps = @(); Files = @(); Everything = $false }

    function Short-Path([string]$p) {
        if ($p.Length -le 44) { return $p }
        return '...' + $p.Substring($p.Length - 41)
    }

    # Everything search syntax: words are AND-ed, a term with a backslash matches the full path, ! excludes.
    $AppFilter  = 'ext:lnk;url;appref-ms !"\Recent\" !"\$Recycle.Bin\" !"\SendTo\"'
    $FileFilter = '!ext:lnk;url;appref-ms;exe;dll;sys;tmp;log;ini;dat;db;cache !"\AppData\" !"C:\Windows\" !"C:\ProgramData\" !"\$Recycle.Bin\" !"\node_modules\" !"\.git\" !"\WinSxS\"'

    function Render-Search {
        $s = $script:Search
        $items = @()
        $items += $s.Local
        $items += $s.Apps
        if (@($s.Files).Count -gt 0) {
            $items += [pscustomobject]@{ Header = 'Files and folders' }
            $items += $s.Files
        }
        $script:SearchHits = @($items | Where-Object { -not $_.Header })
        if ($s.Everything) { $src = 'searching your launcher + Everything' }
        elseif ($UseEverything) { $src = 'Everything is not running - launcher folders only' }
        else { $src = 'launcher folders' }
        $script:Mode = 'Search'
        Set-ActiveTile ''
        Show-Drawer 'Search' ('{0} matches for "{1}"' -f $script:SearchHits.Count, $s.Q) $src $items
    }

    function Run-Search {
        $q = $ui.Search.Text.Trim()
        if ($q.Length -eq 0) { return }
        $s = $script:Search
        $s.Q = $q
        $s.Local = @(Get-AllItems | Where-Object { $_.Name.IndexOf($q, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object -First 24)
        $s.Apps = @(); $s.Files = @(); $s.Everything = $false
        if ($UseEverything) {
            $term = ($q -replace '"', '')
            try {
                $s.Everything = [CoralNative]::EverythingSearch($script:Hwnd, 1, "$term $AppFilter", 60)
                if ($s.Everything -and $FileResults) { [void][CoralNative]::EverythingSearch($script:Hwnd, 2, "$term $FileFilter", 24) }
            } catch { Write-Log "Everything search failed: $_" }
        }
        Render-Search
    }

    # Everything answers asynchronously; this runs on the UI thread when each reply arrives.
    $onEverything = {
        param([int]$kind, [string[]]$paths, [int[]]$flags)
        if ($script:Mode -ne 'Search') { return }
        $s = $script:Search
        $q = $s.Q
        if ($kind -eq 1) {
            $seen = @{}
            foreach ($l in $s.Local) { $seen[$l.Name.ToLower()] = $true }
            $apps = New-Object System.Collections.ArrayList
            for ($i = 0; $i -lt $paths.Count; $i++) {
                $p = $paths[$i]
                $name = [System.IO.Path]::GetFileNameWithoutExtension($p)
                if ($seen.ContainsKey($name.ToLower())) { continue }
                if ($name -match 'uninstall' -and $q -notmatch 'uninstall') { continue }
                $seen[$name.ToLower()] = $true
                [void]$apps.Add([pscustomobject]@{ Name = $name; Path = $p; Sub = Short-Path (Split-Path -Parent $p) })
            }
            # names that start with what you typed first, then the rest
            $s.Apps = @($apps | Sort-Object @{ Expression = { -not $_.Name.StartsWith($q, [System.StringComparison]::OrdinalIgnoreCase) } }, Name | Select-Object -First 24)
        }
        elseif ($kind -eq 2) {
            $files = New-Object System.Collections.ArrayList
            for ($i = 0; $i -lt $paths.Count; $i++) {
                $p = $paths[$i]
                $isFolder = ($flags[$i] -band 1) -ne 0
                $parent = Split-Path -Parent $p
                if ($isFolder) { $sub = 'Folder in ' + (Short-Path $parent) } else { $sub = Short-Path $parent }
                [void]$files.Add([pscustomobject]@{ Name = (Split-Path -Leaf $p); Path = $p; Sub = $sub })
            }
            $s.Files = @($files)
        }
        Render-Search
    }
    [CoralNative]::OnEverything = $onEverything   # PowerShell turns the script block into the delegate

    # wait for a short pause in typing before searching
    $searchTimer = New-Object System.Windows.Threading.DispatcherTimer
    $searchTimer.Interval = [TimeSpan]::FromMilliseconds(150)
    $searchTimer.Add_Tick({ $searchTimer.Stop(); try { Run-Search } catch { Write-Log "search: $_" } })

    function Update-Search {
        $q = $ui.Search.Text.Trim()
        if ($q.Length -gt 0) { $ui.SearchHint.Visibility = 'Collapsed' } else { $ui.SearchHint.Visibility = 'Visible' }
        $searchTimer.Stop()
        if ($q.Length -eq 0) { if ($script:Mode -eq 'Search') { Close-Drawer }; return }
        $searchTimer.Start()
    }

    # ---------------- build the bar ----------------
    $Glyphs = @{ Games = [char]0xE7FC; Apps = [char]0xE71D; Coding = [char]0xE943; Creative = [char]0xE790; Drives = [char]0xEDA2 }

    function New-Tile([string]$key) {
        $btn = New-Object System.Windows.Controls.Button
        $btn.Style = $win.Resources['Tile']
        $btn.Tag = $key
        $btn.ToolTip = $key
        $sp = New-Object System.Windows.Controls.StackPanel
        $sp.HorizontalAlignment = 'Center'; $sp.VerticalAlignment = 'Center'
        $g = New-Object System.Windows.Controls.TextBlock
        $g.FontFamily = 'Segoe MDL2 Assets'; $g.FontSize = 26; $g.HorizontalAlignment = 'Center'
        if ($Glyphs.ContainsKey($key)) { $g.Text = [string]$Glyphs[$key] } else { $g.Text = [string][char]0xE8B7 }
        $l = New-Object System.Windows.Controls.TextBlock
        $l.Text = $key; $l.FontSize = 13; $l.FontWeight = 'SemiBold'; $l.Margin = '0,6,0,0'; $l.HorizontalAlignment = 'Center'
        [void]$sp.Children.Add($g); [void]$sp.Children.Add($l)
        $btn.Content = $sp
        $btn.Add_Click({ param($s, $e) Toggle ([string]$s.Tag) })
        $script:TileButtons[$key] = $btn
        [void]$ui.Tiles.Children.Add($btn)
    }

    function Build-Bar {
        $ui.Tiles.Children.Clear(); $ui.Pins.Children.Clear(); $script:TileButtons = @{}
        foreach ($d in (Get-Categories)) { New-Tile $d.Name }
        New-Tile 'Drives'

        $all = @(Get-AllItems)
        foreach ($p in @($cfg.pinned)) {
            $hit = $all | Where-Object { $_.Name -eq $p } | Select-Object -First 1
            if (-not $hit) { $hit = $all | Where-Object { $_.Name.IndexOf($p, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object -First 1 }
            if (-not $hit -and (Test-Path -LiteralPath $p)) { $hit = [pscustomobject]@{ Name = [System.IO.Path]::GetFileNameWithoutExtension($p); Path = $p } }
            if (-not $hit) { Write-Log "pinned item not found: $p"; continue }
            $btn = New-Object System.Windows.Controls.Button
            $btn.Style = $win.Resources['Pin']
            $btn.Tag = $hit.Path
            $btn.ToolTip = $hit.Name
            $img = New-Object System.Windows.Controls.Image
            $img.Width = 40; $img.Height = 40
            $img.Source = Get-ItemIcon $hit.Path
            [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($img, 'HighQuality')
            $btn.Content = $img
            $btn.Add_Click({ param($s, $e) Start-Item ([string]$s.Tag) })
            [void]$ui.Pins.Children.Add($btn)
        }
    }

    # ---------------- events ----------------
    $ui.CloseBtn.Add_Click({ Close-Drawer })
    $ui.Search.Add_TextChanged({ Update-Search })
    $win.Add_PreviewKeyDown({
        param($s, $e)
        if ($e.Key -eq 'Escape') {
            $ui.Search.Text = ''; Close-Drawer; $e.Handled = $true
        }
        elseif ($e.Key -eq 'Return' -and $script:Mode -eq 'Search' -and @($script:SearchHits).Count -gt 0) {
            $first = @($script:SearchHits)[0].Path
            $ui.Search.Text = ''
            Start-Item $first
            $e.Handled = $true
        }
    })

    # ---------------- keyboard lighting (iCUE SDK) ----------------
    # the chosen theme lives in rgb.json so it survives restarts; brightness comes from config.json
    $RgbFile = Join-Path $Root 'rgb.json'
    $script:RgbTheme = 'sunset'
    try { if (Test-Path -LiteralPath $RgbFile) { $t = (Get-Content -LiteralPath $RgbFile -Raw | ConvertFrom-Json).theme; if ($t) { $script:RgbTheme = [string]$t } } } catch {}
    $RgbNames = [ordered]@{ 'sunset' = 'Coral Sunset'; 'ocean' = 'Ocean Waves'; 'goldenhour' = 'Golden Hour (follows the time of day)'; 'lagoon' = 'Lagoon (gaming keys)'; 'off' = 'Off - use my iCUE profile' }
    $script:LightMenu = New-Object System.Windows.Controls.MenuItem
    $script:LightMenu.Header = 'Keyboard lighting'
    $script:LightStatus = New-Object System.Windows.Controls.MenuItem
    $script:LightStatus.IsEnabled = $false
    [void]$script:LightMenu.Items.Add($script:LightStatus)
    [void]$script:LightMenu.Items.Add((New-Object System.Windows.Controls.Separator))
    $script:LightItems = @{}
    foreach ($id in $RgbNames.Keys) {
        $mi = New-Object System.Windows.Controls.MenuItem
        $mi.Header = $RgbNames[$id]
        $mi.Tag = $id
        $mi.IsCheckable = $true
        $mi.IsChecked = ($id -eq $script:RgbTheme)
        $mi.Add_Click({
            param($sender, $e)
            $script:RgbTheme = [string]$sender.Tag
            foreach ($o in $script:LightItems.Values) { $o.IsChecked = ([string]$o.Tag -eq $script:RgbTheme) }
            [CoralRgb]::SetTheme($script:RgbTheme)
            try { (@{ theme = $script:RgbTheme } | ConvertTo-Json) | Set-Content -LiteralPath (Join-Path $Root 'rgb.json') -Encoding UTF8 } catch {}
        })
        $script:LightItems[$id] = $mi
        [void]$script:LightMenu.Items.Add($mi)
    }
    $script:LightMenu.Add_SubmenuOpened({ $script:LightStatus.Header = 'iCUE: ' + [CoralRgb]::Status })
    [CoralServer]::LogFile = $LogFile
    if ($cfg.sensorsPort) { [CoralServer]::SensorsPort = [int]$cfg.sensorsPort }
    # picture folders for the wallpaper's photo frames (only images inside these are served)
    $pf = @($cfg.photoFolders | Where-Object { $_ } | ForEach-Object { [Environment]::ExpandEnvironmentVariables([string]$_) })
    if (-not $pf.Count) { $pf = @([Environment]::GetFolderPath('MyPictures')) }
    [CoralServer]::PhotoFolders = [string[]]$pf
    if ($cfg.keyboardLighting -ne $false) {
        $bright = 100; if ($cfg.rgbBrightness) { $bright = [int]$cfg.rgbBrightness }
        [void][CoralRgb]::Start($Root, $script:RgbTheme, $bright)
    } else { [CoralRgb]::Status = 'switched off in config.json (keyboardLighting)' }

    # right-click menu on the bar
    $menu = New-Object System.Windows.Controls.ContextMenu
    $menuEntries = [ordered]@{
        'Refresh'              = { try { $script:cfg = Get-Content -LiteralPath (Join-Path $Root 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json; [CoralServer]::SetCalendar((Get-CalendarUrls $script:cfg)) } catch { Write-Log "settings reload: $_" }; $script:IconCache = @{}; Build-Bar; if ($script:Mode -and $script:Mode -ne 'Search') { $m = $script:Mode; $script:Mode = $null; Toggle $m } }
        'Open launcher folder' = { Start-Process explorer.exe -ArgumentList ('"' + $LaunchRoot + '"') }
        'Edit settings'        = { Start-Process notepad.exe -ArgumentList ('"' + (Join-Path $Root 'config.json') + '"') }
        'Move launcher'        = { Set-MoveMode (-not $script:MoveMode) }
        'Reset position'       = { Set-MoveMode $false; Reset-Pos }
        'Exit Coral Launcher'  = { $win.Close() }
    }
    foreach ($key in $menuEntries.Keys) {
        if ($key -eq 'Exit Coral Launcher') { [void]$menu.Items.Add($script:LightMenu); [void]$menu.Items.Add((New-Object System.Windows.Controls.Separator)) }
        $mi = New-Object System.Windows.Controls.MenuItem
        $mi.Header = $key
        $mi.Add_Click($menuEntries[$key])
        if ($key -eq 'Move launcher') { $script:MoveItem = $mi }
        [void]$menu.Items.Add($mi)
    }
    $ui.Bar.ContextMenu = $menu

    # ---------------- position ----------------
    # Default: bottom-centre of the main screen. A position you drag it to is kept in launcher-position.json.
    $PosFile = Join-Path $Root 'launcher-position.json'
    $Grid = 20

    function Get-SavedPos {
        try { if (Test-Path -LiteralPath $PosFile) { return (Get-Content -LiteralPath $PosFile -Raw | ConvertFrom-Json) } } catch {}
        return $null
    }

    # keep the bar (the bottom 118 px of the window) on screen
    function Fit-Pos([double]$x, [double]$y) {
        $vl = [System.Windows.SystemParameters]::VirtualScreenLeft;  $vt = [System.Windows.SystemParameters]::VirtualScreenTop
        $vw = [System.Windows.SystemParameters]::VirtualScreenWidth; $vh = [System.Windows.SystemParameters]::VirtualScreenHeight
        $x = [math]::Min([math]::Max($x, $vl), $vl + $vw - $win.Width)
        $y = [math]::Min([math]::Max($y, $vt - ($win.Height - 118)), $vt + $vh - $win.Height)
        return @($x, $y)
    }

    function Place-Window {
        $saved = Get-SavedPos
        if ($saved -and $saved.left -ne $null -and $saved.top -ne $null) {
            $p = Fit-Pos ([double]$saved.left) ([double]$saved.top)
        } else {
            $wa = [System.Windows.SystemParameters]::WorkArea
            $p = @(($wa.Left + ($wa.Width - $win.Width) / 2), ($wa.Bottom - $win.Height - [double]$cfg.bottomMargin))
        }
        # (Left/Top are NaN until the window is first placed)
        if ([double]::IsNaN($win.Left) -or [math]::Abs($win.Left - $p[0]) -gt 0.5) { $win.Left = $p[0] }
        if ([double]::IsNaN($win.Top) -or [math]::Abs($win.Top - $p[1]) -gt 0.5)  { $win.Top  = $p[1] }
    }
    Place-Window

    # ---------------- move mode (drag the bar; it snaps to a 20 px grid) ----------------
    $script:MoveMode = $false
    $teal = $bc.ConvertFromString('#1D7364')
    $script:BarLine = $ui.Bar.BorderBrush; $script:BarThick = $ui.Bar.BorderThickness

    function Set-MoveMode([bool]$on) {
        if ($script:MoveMode -eq $on) { return }
        $script:MoveMode = $on
        if ($on) { Close-Drawer; $ui.Search.Text = '' }
        foreach ($el in @($ui.Tiles, $ui.Pins, $ui.Search)) { $el.IsHitTestVisible = -not $on }
        if ($on) {
            $ui.Bar.BorderBrush = $teal; $ui.Bar.BorderThickness = '2'; $ui.Bar.Cursor = [System.Windows.Input.Cursors]::SizeAll
            $ui.Bar.ToolTip = 'Drag to move. Right-click > Done moving when finished.'
        } else {
            $ui.Bar.BorderBrush = $script:BarLine; $ui.Bar.BorderThickness = $script:BarThick; $ui.Bar.Cursor = $null; $ui.Bar.ToolTip = $null
        }
        $script:MoveItem.Header = if ($on) { 'Done moving' } else { 'Move launcher' }
    }

    function Save-Pos {
        $p = Fit-Pos ([math]::Round($win.Left / $Grid) * $Grid) ([math]::Round($win.Top / $Grid) * $Grid)
        $win.Left = $p[0]; $win.Top = $p[1]
        try { (@{ left = $p[0]; top = $p[1] } | ConvertTo-Json) | Set-Content -LiteralPath $PosFile -Encoding UTF8 } catch { Write-Log "save position: $_" }
    }

    function Reset-Pos {
        try { if (Test-Path -LiteralPath $PosFile) { Remove-Item -LiteralPath $PosFile } } catch {}
        Place-Window
    }

    $ui.Bar.Add_MouseLeftButtonDown({
        param($s, $e)
        if (-not $script:MoveMode) { return }
        try { $win.DragMove() } catch {}
        Save-Pos
    })

    # bring the launcher in front of everything with the search box ready (hotkey and taskbar button)
    function Summon-Launcher {
        if ($script:MoveMode) { return }
        $win.Activate() | Out-Null
        [CoralNative]::ForceForeground($script:Hwnd)
        $ui.Search.Focus() | Out-Null
        [System.Windows.Input.Keyboard]::Focus($ui.Search) | Out-Null
    }

    # Start menu shortcut for pinning to the taskbar: it runs "CoralLauncher.vbs show", which calls /show
    try {
        $lnkPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Coral Launcher.lnk'
        $wsh = New-Object -ComObject WScript.Shell
        $want = '"' + (Join-Path $Root 'CoralLauncher.vbs') + '" show'
        $lnk = $wsh.CreateShortcut($lnkPath)
        if (-not (Test-Path -LiteralPath $lnkPath) -or $lnk.Arguments -ne $want) {
            $lnk.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
            $lnk.Arguments = $want
            $lnk.WorkingDirectory = $Root
            $lnk.IconLocation = (Join-Path $Root 'coral.ico') + ',0'
            $lnk.Description = 'Open Coral Launcher'
            $lnk.Save()
            Copy-Item -LiteralPath $lnkPath -Destination (Join-Path $Root 'Coral Launcher.lnk') -Force
            Write-Log 'created the Coral Launcher shortcut (Start menu and launcher folder) - right-click it > Pin to taskbar'
        }
    } catch { Write-Log "shortcut: $_" }

    $script:Hwnd = [IntPtr]::Zero
    $win.Add_SourceInitialized({
        $script:Hwnd = (New-Object System.Windows.Interop.WindowInteropHelper($win)).Handle
        [CoralNative]::MakeToolWindow($script:Hwnd)
        if ($Layer -eq 'topmost') { [CoralNative]::SetTopmost($script:Hwnd, $true) }
        Write-Log ('window at {0},{1} size {2}x{3}, layer {4}' -f $win.Left, $win.Top, $win.Width, $win.Height, $Layer)
        $hk = [string]$cfg.hotkey
        if ($hk -and $hk -ne 'False' -and $hk -ne 'off') {
            [CoralNative]::OnHotkey = [Action]{ Summon-Launcher }
            # parse e.g. "Ctrl+Shift+Space", "Win+Alt+L", "Ctrl+Alt+F2"
            $mods = 0; $vk = 0
            foreach ($part in ($hk -split '\+')) {
                switch -Regex ($part.Trim()) {
                    '^(ctrl|control)$' { $mods = $mods -bor 2; break }
                    '^alt$'            { $mods = $mods -bor 1; break }
                    '^shift$'          { $mods = $mods -bor 4; break }
                    '^(win|windows)$'  { $mods = $mods -bor 8; break }
                    '^space$'          { $vk = 0x20; break }
                    '^f([1-9]|1[0-2])$' { $vk = 0x6F + [int]$Matches[1]; break }
                    '^[a-z0-9]$'       { $vk = [int][char]$part.Trim().ToUpper(); break }
                }
            }
            if ($vk -eq 0) { Write-Log "hotkey '$hk' not understood" }
            elseif (-not [CoralNative]::HookHotkey($script:Hwnd, [uint32]$mods, [uint32]$vk)) { Write-Log "$hk is already used by another app - pick a different hotkey in config.json." }
            else { Write-Log "hotkey $hk ready" }
        }
    })

    # ---- "Add a task" box for the wallpaper's to-do list ----
    # Lively doesn't reliably pass typing to a wallpaper, so clicking "+ Add a task" asks us
    # (POST /ask) to show this small box by the mouse; the wallpaper picks up the text (GET /ask).
    $AskXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent" Topmost="True"
        ShowInTaskbar="False" ResizeMode="NoResize" SizeToContent="WidthAndHeight" Title="Add a task">
  <Border Margin="12" CornerRadius="14" Background="#FFF8F0" BorderBrush="#1D7364" BorderThickness="1.5" Padding="16,12,16,12">
    <Border.Effect><DropShadowEffect BlurRadius="18" ShadowDepth="4" Opacity="0.28" Color="#3B1F24"/></Border.Effect>
    <StackPanel Width="320">
      <TextBlock x:Name="AskTitle" Text="ADD A TASK" FontFamily="Segoe UI" FontSize="11" FontWeight="SemiBold" Foreground="#1D7364" Margin="0,0,0,8"/>
      <TextBox x:Name="AskText" MaxLength="80" FontFamily="Segoe UI" FontSize="15" Padding="8,6" Foreground="#3B1F24"
               Background="#FFFDF9" BorderBrush="#E3C9B8" BorderThickness="1" CaretBrush="#1D7364"/>
      <TextBlock x:Name="AskHint" Text="Enter to add  &#183;  Esc to cancel" FontFamily="Segoe UI" FontSize="11" Foreground="#6B4A44" Margin="0,8,0,0"/>
    </StackPanel>
  </Border>
</Window>
'@
    $script:AskWin = $null
    # (plain script-scope variables, not closures: $script: inside GetNewClosure points somewhere else)
    function Finish-Ask([string]$text, [bool]$cancel) {
        if (-not $script:AskWin) { return }
        $w = $script:AskWin
        $script:AskWin = $null
        [CoralServer]::Answer($text, $cancel)
        $w.Close()
    }
    function Show-AskBox {
        if ($script:AskWin) {
            [CoralNative]::ForceForeground((New-Object System.Windows.Interop.WindowInteropHelper($script:AskWin)).Handle)
            $script:AskWin.Activate() | Out-Null; [System.Windows.Input.Keyboard]::Focus($script:AskTb) | Out-Null
            return
        }
        $w = [Windows.Markup.XamlReader]::Parse($AskXaml)
        $script:AskWin = $w
        $script:AskTb = $w.FindName('AskText')
        $w.FindName('AskTitle').Text = [CoralServer]::AskTitle
        if ([CoralServer]::AskHint) { $w.FindName('AskHint').Text = [CoralServer]::AskHint }
        $script:AskTb.Add_KeyDown({
            param($sender, $e)
            if ($e.Key -eq 'Return') {
                $t = $script:AskTb.Text.Trim()
                if ($t) { Finish-Ask $t $false } else { Finish-Ask '' $true }
                $e.Handled = $true
            } elseif ($e.Key -eq 'Escape') { Finish-Ask '' $true; $e.Handled = $true }
        })
        $w.Add_Deactivated({ Finish-Ask '' $true })    # clicking away cancels
        # open just below the mouse (the click on "+ Add a task"), kept on screen
        $px = [CoralNative]::CursorPos()
        $m = [System.Windows.PresentationSource]::FromVisual($win).CompositionTarget.TransformFromDevice
        $pt = $m.Transform((New-Object System.Windows.Point($px[0], $px[1])))
        $vl = [System.Windows.SystemParameters]::VirtualScreenLeft; $vt = [System.Windows.SystemParameters]::VirtualScreenTop
        $vw = [System.Windows.SystemParameters]::VirtualScreenWidth; $vh = [System.Windows.SystemParameters]::VirtualScreenHeight
        $w.Left = [math]::Max($vl, [math]::Min($pt.X - 60, $vl + $vw - 380))
        $w.Top  = [math]::Max($vt, [math]::Min($pt.Y + 8, $vt + $vh - 170))
        $w.Show()
        [CoralNative]::ForceForeground((New-Object System.Windows.Interop.WindowInteropHelper($w)).Handle)
        $w.Activate() | Out-Null
        [System.Windows.Input.Keyboard]::Focus($script:AskTb) | Out-Null
    }

    # keep the launcher on the desktop layer (behind apps, in front of the wallpaper)
    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(400)
    $script:Ticks = 0
    $script:SeenEdit = 0
    $script:SeenAsk = [CoralServer]::AskVersion
    $script:SeenShow = [CoralServer]::ShowVersion
    $script:CfgTime = (Get-Item -LiteralPath (Join-Path $Root 'config.json')).LastWriteTimeUtc
    $timer.Add_Tick({
        try {
            if ($quit.WaitOne(0)) { $win.Close(); return }
            if ($Layer -eq 'desktop') { [CoralNative]::KeepOnDesktop($script:Hwnd) }
            $script:Ticks++
            if ($Show -and $script:Ticks -eq 3) { Summon-Launcher }   # started from the taskbar button
            if ([CoralServer]::ShowVersion -ne $script:SeenShow) { $script:SeenShow = [CoralServer]::ShowVersion; Summon-Launcher }
            if ([CoralServer]::AskVersion -ne $script:SeenAsk) {
                $script:SeenAsk = [CoralServer]::AskVersion
                try { Show-AskBox } catch { Write-Log "add-task box: $_"; [CoralServer]::Answer('', $true) }
            }
            if ([CoralServer]::EditVersion -ne $script:SeenEdit) {
                $script:SeenEdit = [CoralServer]::EditVersion
                if ([CoralServer]::ResetLauncher) { [CoralServer]::ResetLauncher = $false; Set-MoveMode $false; Reset-Pos }
                Set-MoveMode ([CoralServer]::EditMode)
            }
            # every ~5 s: re-check the position (at sign-in the taskbar and screens may not be ready yet)
            if (-not $script:MoveMode -and ($script:Ticks % 12) -eq 0) { Place-Window }
            # every ~5 s: pick up edits to config.json (calendar address etc.) without a restart
            if (($script:Ticks % 12) -eq 6) {
                $t = (Get-Item -LiteralPath (Join-Path $Root 'config.json')).LastWriteTimeUtc
                if ($t -ne $script:CfgTime) {
                    $script:CfgTime = $t
                    try {
                        $script:cfg = Get-Content -LiteralPath (Join-Path $Root 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
                        Copy-Item -LiteralPath (Join-Path $Root 'config.json') -Destination (Join-Path $Root 'config.lastgood.json') -Force
                        [CoralServer]::SetCalendar((Get-CalendarUrls $script:cfg))
                        Write-Log ("settings reloaded (" + @(Get-CalendarUrls $script:cfg).Count + " calendar(s))")
                    } catch { Write-Log "settings reload: $_" }
                }
            }
            if ($script:Ticks -eq 8) { Write-Log ("window z-order (top first):`r`n" + [CoralNative]::DumpZOrder($script:Hwnd)) }
        } catch { Write-Log "tick: $_" }
    })

    $win.Add_Deactivated({ if ($script:Mode -eq 'Search') { $ui.Search.Text = '' } })
    $win.Add_Closed({ try { [CoralRgb]::Stop() } catch {}; $timer.Stop(); try { [CoralNative]::UnhookHotkey($script:Hwnd) } catch {}; try { [CoralServer]::Stop() } catch {} })

    Build-Bar

    # local service for the Coral Coast wallpaper: saved to-do state + Google Calendar feed
    $port = 47613
    if ($cfg.wallpaperPort) { $port = [int]$cfg.wallpaperPort }
    [CoralServer]::LogFile = $LogFile
    if ([CoralServer]::Start($port, $Root, (Get-CalendarUrls $cfg))) { Write-Log "wallpaper service on http://127.0.0.1:$port" }

    Write-Log ('started - {0} categories, launch root {1}' -f $script:TileButtons.Count, $LaunchRoot)
    $timer.Start()
    [void]$win.ShowDialog()
}
catch {
    Write-Log ("ERROR: " + $_.Exception.Message + "`r`n" + $_.ScriptStackTrace + "`r`n" + $_.InvocationInfo.PositionMessage)
    if ($Debug) { throw }
}
finally {
    try { $mutex.ReleaseMutex() } catch {}
    try { $mutex.Dispose(); $quit.Dispose() } catch {}
}
