using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.ArkPayServer.Notifications;

public class ArkadeDestinationDisabledNotification : BaseNotification
{
    private const string TYPE = "arkade-destination-disabled";

    public string StoreId { get; set; } = "";
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
        : NotificationHandler<ArkadeDestinationDisabledNotification>
    {
        public override string NotificationType => TYPE;
        public override (string identifier, string name)[] Meta =>
            [(TYPE, "Arkade destination disabled")];

        protected override void FillViewModel(
            ArkadeDestinationDisabledNotification notification,
            NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = "An Arkade signer rotation disabled this store's sweep destination. " +
                      "Review and re-confirm a destination to resume sweeping.";
            vm.ActionLink = linkGenerator.GetPathByAction(
                action: nameof(Controllers.ArkController.StoreOverview),
                controller: "Ark",
                values: new { storeId = notification.StoreId },
                options.RootPath);
        }
    }
}
