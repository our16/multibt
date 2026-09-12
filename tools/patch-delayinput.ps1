$ErrorActionPreference = 'Stop'

# Delay becomes a number field with step buttons instead of a slider.
#
# A slider is the wrong control for this: the range is 0-2000 ms and the useful precision is about 5 ms, so
# one pixel of thumb travel is worth more than the step you want, and hitting an exact value is impossible.
# A field accepts a typed value and the two buttons cover the common nudge.

$devFile = 'src\MultiBT.App\ViewModels\DeviceViewModel.cs'
$xaml    = 'src\MultiBT.App\MainWindow.xaml'
$behind  = 'src\MultiBT.App\MainWindow.xaml.cs'

$texts = @{}
foreach ($p in @($devFile, $xaml, $behind)) {
    $raw = [System.IO.File]::ReadAllText($p)
    $texts[$p] = @{ Text = $raw.Replace("`r`n", "`n"); Crlf = $raw.Contains("`r`n") }
}
$script:edits = @()
function Plan($p, $old, $new, $label) {
    $c = ([regex]::Matches($texts[$p].Text, [regex]::Escape($old))).Count
    if ($c -eq 0) { throw "$label : anchor not found" }
    if ($c -ne 1) { throw "$label : anchor not unique ($c)" }
    $script:edits += ,@($p, $old, $new, $label)
}

# ---------------------------------------------------------------- 1. stepping in the view model
Plan $devFile @'
    /// <summary>This device's position to the listener's right, in metres.</summary>
'@ @'
    /// <summary>How far one press of the delay step buttons moves the value, in ms.</summary>
    public const double DelayStepMs = 5.0;

    /// <summary>Adds one step to the manual delay, stopping at the configured maximum.</summary>
    public void IncreaseDelay() => ManualOffsetMs = Math.Clamp(ManualOffsetMs + DelayStepMs, 0.0, 2000.0);

    /// <summary>Subtracts one step from the manual delay, stopping at zero.</summary>
    public void DecreaseDelay() => ManualOffsetMs = Math.Clamp(ManualOffsetMs - DelayStepMs, 0.0, 2000.0);

    /// <summary>This device's position to the listener's right, in metres.</summary>
'@ 'device: delay step methods'

# ---------------------------------------------------------------- 2. the row: field + step buttons
Plan $xaml @'
                                    <TextBlock Style="{StaticResource FieldLabel}" Margin="0,0,6,0"
                                               Text="{Binding Source={x:Static loc:Localizer.Instance}, Path=[Delay.Label]}" />
                                    <Slider Width="132" Minimum="0" Maximum="2000" SmallChange="1" LargeChange="50"
                                            Value="{Binding ManualOffsetMs, Mode=TwoWay}" VerticalAlignment="Center"
                                            ToolTip="{Binding Source={x:Static loc:Localizer.Instance}, Path=[Delay.Tip]}" />
                                    <TextBlock Style="{StaticResource ValueText}" Width="54" Margin="6,0,0,0"
                                               Text="{Binding DelayLabel}" />
'@ @'
                                    <TextBlock Style="{StaticResource FieldLabel}" Margin="0,0,6,0"
                                               Text="{Binding Source={x:Static loc:Localizer.Instance}, Path=[Delay.Label]}" />
                                    <!--
                                      A field rather than a slider: 0-2000 ms with about 5 ms of useful precision, so a
                                      slider's thumb travel is coarser than the step anyone wants and an exact value
                                      cannot be hit. The two buttons cover the common nudge.
                                    -->
                                    <Button Width="26" Height="24" Padding="0" FontSize="14"
                                            Style="{StaticResource BaseButton}" Click="OnDelayDown"
                                            Content="&#x2212;" VerticalAlignment="Center"
                                            ToolTip="{Binding Source={x:Static loc:Localizer.Instance}, Path=[Delay.Down.Tip]}" />
                                    <TextBox Width="58" Height="24" Margin="4,0,4,0" FontSize="12"
                                             VerticalContentAlignment="Center" TextAlignment="Right"
                                             Text="{Binding ManualOffsetMs, Mode=TwoWay, StringFormat={}{0:0}}"
                                             ToolTip="{Binding Source={x:Static loc:Localizer.Instance}, Path=[Delay.Tip]}" />
                                    <Button Width="26" Height="24" Padding="0" FontSize="14"
                                            Style="{StaticResource BaseButton}" Click="OnDelayUp"
                                            Content="+" VerticalAlignment="Center"
                                            ToolTip="{Binding Source={x:Static loc:Localizer.Instance}, Path=[Delay.Up.Tip]}" />
                                    <TextBlock Style="{StaticResource FieldLabel}" Margin="4,0,0,0" Text="ms" />
'@ 'xaml: delay field with step buttons'

# ---------------------------------------------------------------- 3. handlers
Plan $behind @'
    private void OnCopyNotice(object sender, RoutedEventArgs e)
'@ @'
    /// <summary>Steps one device's delay down by one step.</summary>
    private void OnDelayDown(object sender, RoutedEventArgs e) => StepDelay(sender, up: false);

    /// <summary>Steps one device's delay up by one step.</summary>
    private void OnDelayUp(object sender, RoutedEventArgs e) => StepDelay(sender, up: true);

    /// <summary>
    /// Applies one delay step to the device whose row was clicked.
    /// </summary>
    /// <remarks>
    /// The button lives inside a DataTemplate, so the target device comes from the sender's DataContext rather
    /// than from a field: there is one handler for every row, and the row it was pressed in decides which
    /// device moves.
    /// </remarks>
    private static void StepDelay(object sender, bool up)
    {
        if (sender is not FrameworkElement { DataContext: DeviceViewModel device })
        {
            return;
        }

        if (up)
        {
            device.IncreaseDelay();
        }
        else
        {
            device.DecreaseDelay();
        }
    }

    private void OnCopyNotice(object sender, RoutedEventArgs e)
'@ 'behind: delay step handlers'

foreach ($e in $edits) {
    $entry = $texts[$e[0]]
    $entry.Text = $entry.Text.Replace($e[1], $e[2])
    Write-Output "  ok  $($e[3])"
}
foreach ($p in @($devFile, $xaml, $behind)) {
    $entry = $texts[$p]
    $out = if ($entry.Crlf) { $entry.Text.Replace("`n", "`r`n") } else { $entry.Text }
    [System.IO.File]::WriteAllText($p, $out)
}

# strings for the two button tooltips
$locFile = 'src\MultiBT.App\Localization\Localizer.cs'
$l = [System.IO.File]::ReadAllText($locFile)
$crlf = $l.Contains("`r`n")
$lt = $l.Replace("`r`n", "`n")
foreach ($pair in @(
    @('        ["Master.Label"] = "总音量",', '        ["Delay.Up.Tip"] = "增加 5 ms",' + "`n" + '        ["Delay.Down.Tip"] = "减少 5 ms",' + "`n" + '        ["Master.Label"] = "总音量",'),
    @('        ["Master.Label"] = "Master",', '        ["Delay.Up.Tip"] = "Add 5 ms",' + "`n" + '        ["Delay.Down.Tip"] = "Subtract 5 ms",' + "`n" + '        ["Master.Label"] = "Master",')
)) {
    $c = ([regex]::Matches($lt, [regex]::Escape($pair[0]))).Count
    if ($c -ne 1) { throw "loc anchor not unique ($c)" }
    $lt = $lt.Replace($pair[0], $pair[1])
}
if ($crlf) { $lt = $lt.Replace("`n", "`r`n") }
[System.IO.File]::WriteAllText($locFile, $lt)
Write-Output '  ok  loc: delay step tooltips'

# ---------------------------------------------------------------- verify
$d = [System.IO.File]::ReadAllText($devFile)
$x = [System.IO.File]::ReadAllText($xaml)
$b = [System.IO.File]::ReadAllText($behind)
$l2 = [System.IO.File]::ReadAllText($locFile)

foreach ($need in @('IncreaseDelay', 'DecreaseDelay')) { if (-not $d.Contains($need)) { throw "missing: $need" } }
if ($x.Contains('Value="{Binding ManualOffsetMs, Mode=TwoWay}" VerticalAlignment="Center"')) { throw 'the delay slider is still there' }
foreach ($need in @('OnDelayUp', 'OnDelayDown')) { if (-not $x.Contains($need) -or -not $b.Contains($need)) { throw "missing handler wiring: $need" } }
foreach ($k in @('Delay.Up.Tip', 'Delay.Down.Tip')) {
    $n = ([regex]::Matches($l2, [regex]::Escape('["' + $k + '"]'))).Count
    if ($n -ne 2) { throw "$k found $n times, expected 2" }
}
$o = ([regex]::Matches($b, '\{')).Count; $c2 = ([regex]::Matches($b, '\}')).Count
if ($o -ne $c2) { throw "code-behind brace mismatch: $o vs $c2" }

Write-Output '  ok  verified'
Write-Output ''
Write-Output 'delay-input patch applied'
