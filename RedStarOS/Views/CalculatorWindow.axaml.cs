using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RedStarOS.Views;

/// <summary>Four-function calculator (was Form9).</summary>
public partial class CalculatorWindow : Window
{
    private double _accumulator;
    private string _pendingOp = "";
    private bool _startNewEntry = true;

    public CalculatorWindow() => InitializeComponent();

    private static double Parse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Format(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "Error" : v.ToString("G15", CultureInfo.InvariantCulture);

    private void OnDigit(object? sender, RoutedEventArgs e)
    {
        var d = (string)((Button)sender!).Tag!;
        var current = Display.Text ?? "0";
        if (current == "Error") { current = "0"; _startNewEntry = true; }

        if (_startNewEntry || current == "0")
        {
            Display.Text = d == "." ? "0." : d;
            _startNewEntry = false;
        }
        else if (d != "." || !current.Contains('.'))
        {
            Display.Text = current + d;
        }
    }

    private void OnOperator(object? sender, RoutedEventArgs e)
    {
        // Chain operations: 2 + 3 + ... evaluates the pending operation first.
        if (_pendingOp != "" && !_startNewEntry) Evaluate();
        _accumulator = Parse(Display.Text ?? "0");
        _pendingOp = (string)((Button)sender!).Tag!;
        _startNewEntry = true;
    }

    private void OnEquals(object? sender, RoutedEventArgs e)
    {
        Evaluate();
        _pendingOp = "";
        _startNewEntry = true;
    }

    private void Evaluate()
    {
        var right = Parse(Display.Text ?? "0");
        double result = _pendingOp switch
        {
            "+" => _accumulator + right,
            "-" => _accumulator - right,
            "*" => _accumulator * right,
            "/" => _accumulator / right,
            _ => right
        };
        Display.Text = Format(result);
        _accumulator = double.IsFinite(result) ? result : 0;
    }

    // CE: clear the current entry only.  C: reset everything.
    private void OnClearEntry(object? sender, RoutedEventArgs e)
    {
        Display.Text = "0";
        _startNewEntry = true;
    }

    private void OnClearAll(object? sender, RoutedEventArgs e)
    {
        Display.Text = "0";
        _accumulator = 0;
        _pendingOp = "";
        _startNewEntry = true;
    }
}
