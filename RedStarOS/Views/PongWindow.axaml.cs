using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Ping Pong. The playfield scales with the window. Solo: keep the rally going. Versus: first to 7 against the
/// computer. Mouse or Left/Right/A/D move the racket, P/Space pauses, F1 starts a new game, Esc exits.
/// The game pauses when the window loses focus.
/// </summary>
public partial class PongWindow : Window
{
    private enum State { Ready, Serving, Playing, Paused, Over }

    private const int WinScore = 7;

    /// <summary>How hard it is (Difficulty menu, remembered): the ball's speed and how fast it gets faster, and how
    /// well the computer plays: how often it looks again, how far off its aim is and how fast its racket moves.</summary>
    public sealed record Level(string Name, double Ball, double Speedup, double MaxBall, double AiLook, double AiError, double AiSpeed);

    public static readonly Level[] Levels =
    {
        new("Easy",   0.80, 1.03, 1.7, 0.34, 1.10, 0.38),
        new("Normal", 1.00, 1.06, 2.2, 0.22, 0.70, 0.55),
        new("Hard",   1.15, 1.08, 2.6, 0.14, 0.40, 0.75),
        new("Expert", 1.30, 1.10, 3.0, 0.08, 0.15, 1.00),
    };

    private static Level Current => Levels[Math.Clamp(SettingsStore.Current.PongLevel, 0, Levels.Length - 1)];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng = new();

    private State _state = State.Ready;
    private State _before = State.Ready;
    private bool _vsAi;
    private bool _left, _right;
    private double _last, _serveUntil;

    private double _bx, _by, _vx, _vy, _speed;      // ball centre, velocity (px/s), speed
    private double _aiTarget, _aiRetarget;
    private int _score, _aiScore;

    public PongWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Step();

        Playground.PointerMoved += (_, e) => SetRacketCentre(e.GetPosition(Playground).X);
        Playground.PointerPressed += (_, _) =>
        {
            Focus();
            if (_state == State.Ready) Serve();
            else if (_state == State.Over) NewGame();
        };
        Playground.SizeChanged += (_, _) => RelayOut();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);   // before any focused control (Space would press a button)
        KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.A) _left = false;
            else if (e.Key is Key.Right or Key.D) _right = false;
        };
        Deactivated += (_, _) => { if (_state is State.Playing or State.Serving) TogglePause(); };

        Opened += (_, _) => { Focus(); UpdateLevelMenu(); NewGame(); };
    }

    // ------------------------------------------------------------ geometry

    private double FieldW => Playground.Bounds.Width > 0 ? Playground.Bounds.Width : Width;
    private double FieldH => Playground.Bounds.Height > 0 ? Playground.Bounds.Height : Height;
    private double BallR => Math.Clamp(Math.Min(FieldW, FieldH) * 0.035, 14, 30);
    private double RacketW => Math.Max(90, FieldW * 0.2);
    private double BaseSpeed => Math.Max(260, FieldH * 0.7);
    private double PlayerY => FieldH - 40;
    private double AiY => 26;

    private double RacketCentre => Canvas.GetLeft(Racket) + RacketW / 2;
    private double AiCentre => Canvas.GetLeft(AiRacket) + RacketW / 2;

    private void SetRacketCentre(double cx) =>
        Canvas.SetLeft(Racket, Math.Clamp(cx - RacketW / 2, 0, Math.Max(0, FieldW - RacketW)));

    private void RelayOut()
    {
        Racket.Width = AiRacket.Width = RacketW;
        Canvas.SetTop(Racket, PlayerY);
        Canvas.SetTop(AiRacket, AiY);
        Ball.Width = Ball.Height = BallR * 2;
        Net.Width = FieldW;
        Canvas.SetTop(Net, FieldH / 2);
        BestLabel.Width = 174;
        BestLabel.TextAlignment = TextAlignment.Right;
        Canvas.SetLeft(BestLabel, Math.Max(16, FieldW - 190));
        Net.IsVisible = _vsAi;
        AiRacket.IsVisible = _vsAi;
        PlaceBall();
        PlaceOverlay();
    }

    private void PlaceBall()
    {
        Canvas.SetLeft(Ball, _bx - BallR);
        Canvas.SetTop(Ball, _by - BallR);
    }

    private void PlaceOverlay()
    {
        Overlay.Measure(new Avalonia.Size(FieldW, FieldH));
        Canvas.SetLeft(Overlay, (FieldW - Overlay.DesiredSize.Width) / 2);
        Canvas.SetTop(Overlay, (FieldH - Overlay.DesiredSize.Height) / 2);
    }

    private void ShowOverlay(string? text)
    {
        Overlay.IsVisible = text != null;
        if (text == null) return;
        OverlayText.Text = text;
        PlaceOverlay();
    }

    // ------------------------------------------------------------ game flow

    private void NewGame()
    {
        _score = _aiScore = 0;
        _left = _right = false;
        RelayOut();
        SetRacketCentre(FieldW / 2);
        Canvas.SetLeft(AiRacket, (FieldW - RacketW) / 2);
        _speed = BaseSpeed * Current.Ball;
        ResetBall();
        _state = State.Ready;
        _timer.Stop();
        UpdateLabels();
        UpdateModeMenu();
        ShowOverlay(_vsAi
            ? "Versus the computer - first to 7\nClick or press Space to serve"
            : "Solo - keep the ball in play\nClick or press Space to start");
    }

    private void ResetBall()
    {
        _bx = FieldW / 2;
        _by = _vsAi ? FieldH / 2 : FieldH * 0.25;
        _vx = _vy = 0;
        PlaceBall();
    }

    private void Serve()
    {
        ResetBall();
        _speed = BaseSpeed * Current.Ball;
        var angle = (_rng.NextDouble() - 0.5) * 0.9;
        var down = !_vsAi || _rng.Next(2) == 0;
        _vx = _speed * Math.Sin(angle);
        _vy = (down ? 1 : -1) * _speed * Math.Cos(angle);
        _last = _clock.Elapsed.TotalSeconds;
        _serveUntil = _last + 0.8;                    // short pause before the ball moves
        _state = State.Serving;
        ShowOverlay(null);
        _timer.Start();
    }

    private void TogglePause()
    {
        if (_state == State.Paused)
        {
            _state = _before;
            _last = _clock.Elapsed.TotalSeconds;
            ShowOverlay(null);
            _timer.Start();
        }
        else if (_state is State.Playing or State.Serving)
        {
            _before = _state;
            _state = State.Paused;
            _timer.Stop();
            ShowOverlay("Paused\nPress P or Space to resume");
        }
    }

    private void GameOver(string message)
    {
        _state = State.Over;
        _timer.Stop();
        var s = SettingsStore.Current;
        if (!_vsAi && _score > s.PongBest) { s.PongBest = _score; SettingsStore.Save(); message += "\nNew best!"; }
        UpdateLabels();
        ShowOverlay(message + "\nF1 or click for a new game");
    }

    private void UpdateLabels()
    {
        ScoreLabel.Text = _vsAi ? $"{_score}  :  {_aiScore}" : _score.ToString();
        BestLabel.Text = (_vsAi ? $"Wins vs computer: {SettingsStore.Current.PongAiWins}" : $"Best rally: {SettingsStore.Current.PongBest}") + $" · {Current.Name}";
    }

    // ------------------------------------------------------------ simulation

    private void Step()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _last, 0.05);
        _last = now;

        var move = (_right ? 1 : 0) - (_left ? 1 : 0);
        if (move != 0) SetRacketCentre(RacketCentre + move * FieldW * 1.1 * dt);

        if (_vsAi) MoveAi(now, dt);
        if (now < _serveUntil) return;
        _state = State.Playing;

        var r = BallR;
        _bx += _vx * dt;
        _by += _vy * dt;

        if (_bx - r <= 0) { _bx = r; _vx = Math.Abs(_vx); }
        else if (_bx + r >= FieldW) { _bx = FieldW - r; _vx = -Math.Abs(_vx); }

        if (_vy > 0 && _by + r >= PlayerY && _by - r <= PlayerY + 14 && Math.Abs(_bx - RacketCentre) <= RacketW / 2 + r * 0.6)
        {
            _by = PlayerY - r;
            Bounce(RacketCentre, -1);
            if (!_vsAi) { _score++; UpdateLabels(); }
        }
        else if (_vsAi && _vy < 0 && _by - r <= AiY + 14 && _by + r >= AiY && Math.Abs(_bx - AiCentre) <= RacketW / 2 + r * 0.6)
        {
            _by = AiY + 14 + r;
            Bounce(AiCentre, 1);
        }

        if (!_vsAi && _by - r <= 0) { _by = r; _vy = Math.Abs(_vy); }

        PlaceBall();

        if (_by - r >= FieldH)                         // got past the player
        {
            if (!_vsAi) { GameOver($"Game over\nRally: {_score}"); return; }
            _aiScore++;
            Point(playerScored: false);
        }
        else if (_vsAi && _by + r <= 0)                // got past the computer
        {
            _score++;
            Point(playerScored: true);
        }
    }

    /// <summary>Where the ball hits the racket decides its angle; every return is a little faster.</summary>
    private void Bounce(double racketCentre, int direction)
    {
        var offset = Math.Clamp((_bx - racketCentre) / (RacketW / 2), -1, 1);
        _speed = Math.Min(_speed * Current.Speedup, BaseSpeed * Current.MaxBall);
        _vx = _speed * Math.Sin(offset);
        _vy = direction * _speed * Math.Cos(offset);
    }

    private void Point(bool playerScored)
    {
        UpdateLabels();
        if (_score >= WinScore || _aiScore >= WinScore)
        {
            if (_score >= WinScore) { SettingsStore.Current.PongAiWins++; SettingsStore.Save(); }
            GameOver(_score >= WinScore ? "You win!" : "The computer wins");
            return;
        }
        ResetBall();
        _timer.Stop();
        _state = State.Ready;
        ShowOverlay((playerScored ? "Point for you" : "Point for the computer") + "\nClick or press Space to serve");
    }

    /// <summary>The computer re-aims a few times a second with some error and has a limited speed, so it can be beaten.</summary>
    private void MoveAi(double now, double dt)
    {
        if (now >= _aiRetarget)
        {
            _aiRetarget = now + Current.AiLook;
            _aiTarget = _vy <= 0 ? _bx + (_rng.NextDouble() - 0.5) * RacketW * Current.AiError : FieldW / 2;
        }
        var maxStep = FieldW * Current.AiSpeed * dt;
        var cx = AiCentre + Math.Clamp(_aiTarget - AiCentre, -maxStep, maxStep);
        Canvas.SetLeft(AiRacket, Math.Clamp(cx - RacketW / 2, 0, Math.Max(0, FieldW - RacketW)));
    }

    // ------------------------------------------------------------ menu / keys

    private void SetMode(bool vsAi) { _vsAi = vsAi; NewGame(); }

    private void UpdateModeMenu()
    {
        ModeSolo.Header = (_vsAi ? "    " : "✓  ") + "_Solo - keep the rally going";
        ModeAi.Header = (_vsAi ? "✓  " : "    ") + "_Versus the computer - first to 7";
    }

    private void UpdateLevelMenu()
    {
        var items = new[] { LevelEasy, LevelNormal, LevelHard, LevelExpert };
        for (int i = 0; i < items.Length; i++)
            items[i].Header = (i == SettingsStore.Current.PongLevel ? "✓  " : "    ") + "_" + Levels[i].Name;
    }

    private void OnLevel(object? s, RoutedEventArgs e)
    {
        if (s is not MenuItem { Tag: string t } || !int.TryParse(t, out var level)) return;
        SettingsStore.Current.PongLevel = level;
        SettingsStore.Save();
        UpdateLevelMenu();
        NewGame();
        Focus();
    }

    private void OnModeSolo(object? s, RoutedEventArgs e) => SetMode(false);
    private void OnModeAi(object? s, RoutedEventArgs e) => SetMode(true);
    private void OnNewGame(object? s, RoutedEventArgs e) { NewGame(); Focus(); }
    private void OnPause(object? s, RoutedEventArgs e) => TogglePause();
    private void OnExitGame(object? s, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.F1: NewGame(); break;
            case Key.P: TogglePause(); break;
            case Key.Space:
                if (_state == State.Ready) Serve();
                else if (_state == State.Over) NewGame();
                else TogglePause();
                break;
            case Key.Left or Key.A: _left = true; break;
            case Key.Right or Key.D: _right = true; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
