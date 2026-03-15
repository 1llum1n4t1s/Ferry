using System;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.Tests.Services;

/// <summary>
/// StubTransferService のユニットテスト。
/// インターフェース準拠（メソッドが例外なく動作すること）を確認する。
/// </summary>
public sealed class StubTransferServiceTests
{
    [Fact]
    public async Task SendFileAsync_例外を投げない()
    {
        var svc = new StubTransferService();
        var ex = await Record.ExceptionAsync(() => svc.SendFileAsync("dummy.txt"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ResumeTransferAsync_falseを返す()
    {
        var svc = new StubTransferService();
        var result = await svc.ResumeTransferAsync(Guid.NewGuid());
        Assert.False(result);
    }

    [Fact]
    public void HandleReceivedData_例外を投げない()
    {
        var svc = new StubTransferService();
        var ex = Record.Exception(() => svc.HandleReceivedData(new byte[] { 0xFF, 0x00 }));
        Assert.Null(ex);
    }

    [Fact]
    public void GetResumableTransfers_空リストを返す()
    {
        var svc = new StubTransferService();
        var transfers = svc.GetResumableTransfers();
        Assert.Empty(transfers);
    }

    [Fact]
    public void ITransferServiceインターフェースを実装している()
    {
        var svc = new StubTransferService();
        Assert.IsAssignableFrom<ITransferService>(svc);
    }
}
