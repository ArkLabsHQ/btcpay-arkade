using System.Reflection;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Controllers;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Wallets;
using Xunit;

namespace NArk.Tests;

public class ArkWalletCapabilitiesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WalletReportsSigningCapabilityIndependentlyOfAddressDerivation(bool canSign)
    {
        var wallet = new ArkWalletInfo("wallet", null, null, WalletType.HD, null, 0);
        var storage = TestProxy.Create<IWalletStorage>((method, _) => method.Name == "GetWalletById"
            ? Task.FromResult<ArkWalletInfo?>(wallet)
            : throw new NotSupportedException(method.Name));
        var provider = TestProxy.Create<IWalletProvider>((method, _) => method.Name switch
        {
            "GetSignerAsync" => Task.FromResult(canSign ? TestProxy.Create<IArkadeWalletSigner>() : null),
            "GetAddressProviderAsync" => Task.FromResult<IArkadeAddressProvider?>(TestProxy.Create<IArkadeAddressProvider>()),
            _ => throw new NotSupportedException(method.Name)
        });
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, storage);
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(handler, new ArkadePaymentMethodConfig(wallet.Id));
        var constructor = typeof(ArkGreenfieldController).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(PaymentMethodHandlerDictionary) => (object)handlers,
            var type when type == typeof(IWalletStorage) => storage,
            var type when type == typeof(IWalletProvider) => provider,
            _ => null
        }).ToArray();
        var controller = (ArkGreenfieldController)constructor.Invoke(arguments);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.SetStoreData(store);

        var response = Assert.IsType<OkObjectResult>(await controller.GetWallet("store", default));

        Assert.Equal(canSign, Assert.IsType<ArkWalletData>(response.Value).SignerAvailable);
    }
}

public class TestProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> _invoke = (method, _) => throw new NotSupportedException(method.Name);

    public static T Create<T>(Func<MethodInfo, object?[]?, object?>? invoke = null) where T : class
    {
        var proxy = Create<T, TestProxy>();
        if (invoke is not null) ((TestProxy)(object)proxy)._invoke = invoke;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
}
