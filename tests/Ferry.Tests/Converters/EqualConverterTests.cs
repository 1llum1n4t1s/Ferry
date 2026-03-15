using System;
using System.Globalization;
using Ferry.Converters;

namespace Ferry.Tests.Converters;

/// <summary>
/// EqualConverter と NotEqualConverter の値比較ロジックを検証する。
/// </summary>
public class EqualConverterTests
{
    private readonly EqualConverter _converter = new();

    // === EqualConverter ===

    [Fact]
    public void Convert_同値の文字列でtrueを返すこと()
    {
        var result = _converter.Convert("Hello", typeof(bool), "Hello", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_異値の文字列でfalseを返すこと()
    {
        var result = _converter.Convert("Hello", typeof(bool), "World", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_valueがnullの場合falseを返すこと()
    {
        var result = _converter.Convert(null, typeof(bool), "test", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_parameterがnullの場合falseを返すこと()
    {
        var result = _converter.Convert("test", typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_両方nullの場合falseを返すこと()
    {
        var result = _converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_enumの文字列比較でtrueを返すこと()
    {
        var result = _converter.Convert(PeerState.Connected, typeof(bool), "Connected", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_enumの文字列比較で異なる値はfalseを返すこと()
    {
        var result = _converter.Convert(PeerState.Connected, typeof(bool), "Disconnected", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_整数の比較でtrueを返すこと()
    {
        var result = _converter.Convert(42, typeof(bool), "42", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_大文字小文字を区別すること()
    {
        var result = _converter.Convert("Hello", typeof(bool), "hello", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void ConvertBack_NotSupportedExceptionを投げること()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(true, typeof(string), "test", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// NotEqualConverter の値比較ロジックを検証する。
/// </summary>
public class NotEqualConverterTests
{
    private readonly NotEqualConverter _converter = new();

    [Fact]
    public void Convert_同値の文字列でfalseを返すこと()
    {
        var result = _converter.Convert("Hello", typeof(bool), "Hello", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_異値の文字列でtrueを返すこと()
    {
        var result = _converter.Convert("Hello", typeof(bool), "World", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_valueがnullの場合trueを返すこと()
    {
        // null は「等しくない」と判定
        var result = _converter.Convert(null, typeof(bool), "test", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_parameterがnullの場合trueを返すこと()
    {
        var result = _converter.Convert("test", typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_両方nullの場合trueを返すこと()
    {
        // EqualConverter で false → NotEqualConverter で true（一貫性あり）
        var result = _converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_enumの文字列比較で同値はfalseを返すこと()
    {
        var result = _converter.Convert(PeerState.Connected, typeof(bool), "Connected", CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_enumの文字列比較で異なる値はtrueを返すこと()
    {
        var result = _converter.Convert(PeerState.Connected, typeof(bool), "Disconnected", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Convert_大文字小文字を区別すること()
    {
        var result = _converter.Convert("Hello", typeof(bool), "hello", CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void ConvertBack_NotSupportedExceptionを投げること()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(true, typeof(string), "test", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// テスト用の enum（PeerState を直接使用）。
/// </summary>
file enum PeerState
{
    Disconnected,
    Connected,
}
