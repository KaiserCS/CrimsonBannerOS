// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RedStarOS.Views;

/// <summary>A four-function calculator, by mouse or keyboard (digits, + − × ÷, Enter, Backspace, Delete, Esc).</summary>
public partial class CalculatorWindow : Window
{
    private double _accumulator;
    private string _pendingOp = "";
    private bool _startNewEntry = true;

    public CalculatorWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        string? text = e.KeySymbol;
        if (e.Key == Key.Enter) { Compute(); e.Handled = true; return; }
        if (e.Key == Key.Back) { Backspace(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { ClearEntry(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { ClearAll(); e.Handled = true; return; }
        if (text is { Length: 1 })
        {
            char c = text[0];
            if (char.IsDigit(c) || c is '.' or ',') { Digit(c == ',' ? "." : c.ToString()); e.Handled = true; }
            else if (c is '+' or '-' or '*' or '/') { Operator(c.ToString()); e.Handled = true; }
            else if (c == '=') { Compute(); e.Handled = true; }
        }
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> OpSigns = new() { ["+"] = "+", ["-"] = "−", ["*"] = "×", ["/"] = "÷" };

    private static double Parse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Format(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "Error" : v.ToString("G15", CultureInfo.InvariantCulture);

    private void OnDigit(object? sender, RoutedEventArgs e) => Digit((string)((Button)sender!).Tag!);

    private void Digit(string d)
    {
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

    private void OnOperator(object? sender, RoutedEventArgs e) => Operator((string)((Button)sender!).Tag!);

    private void Operator(string op)
    {
        // Chain operations: 2 + 3 + ... evaluates the pending operation first.
        if (_pendingOp != "" && !_startNewEntry) Evaluate();
        _accumulator = Parse(Display.Text ?? "0");
        _pendingOp = op;
        _startNewEntry = true;
        Pending.Text = Format(_accumulator) + " " + OpSigns[op];
    }

    private void OnEquals(object? sender, RoutedEventArgs e) => Compute();

    private void Compute()
    {
        Evaluate();
        _pendingOp = "";
        _startNewEntry = true;
        Pending.Text = " ";
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
    private void OnClearEntry(object? sender, RoutedEventArgs e) => ClearEntry();
    private void OnClearAll(object? sender, RoutedEventArgs e) => ClearAll();
    private void OnBackspace(object? sender, RoutedEventArgs e) => Backspace();

    private void ClearEntry()
    {
        Display.Text = "0";
        _startNewEntry = true;
    }

    private void ClearAll()
    {
        Display.Text = "0";
        _accumulator = 0;
        _pendingOp = "";
        _startNewEntry = true;
        Pending.Text = " ";
    }

    private void Backspace()
    {
        if (_startNewEntry) return;
        var t = Display.Text ?? "0";
        Display.Text = t.Length <= 1 || t == "Error" ? "0" : t[..^1];
        if (Display.Text is "-" or "") Display.Text = "0";
    }
}
