using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class MainWindow
{
    private Control ConnectionAccountControls(AgentSnapshot snapshot, AccountProfile account)
    {
        var content = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var login = snapshot.Logins.FirstOrDefault(l => l.AccountId == account.Id);
        var active = _model.IsDesktopLoginActive(account.Id) || login?.State is "Starting" or "Pending" or "AwaitingUser";
        var actions = new WrapPanel();
        if (account.Provider != AccountProvider.TunnelToken)
        {
            var authenticate = ActionButton("Autenticar conta", async () => await _model.ActionAsync("account.login", account.Id));
            authenticate.IsEnabled = !active;
            actions.Children.Add(authenticate);
        }
        var validate = ActionButton("Validar conta", async () => await _model.ActionAsync("account.validate", account.Id));
        validate.IsEnabled = !active;
        actions.Children.Add(validate);
        if (active) actions.Children.Add(ActionButton("Cancelar login", async () => await _model.ActionAsync("account.cancel", account.Id)));
        content.Children.Add(actions);
        content.Children.Add(new ActivityPanel(_model, () => _model.AccountActivity(account.Id)));
        if (login?.Challenge is { } challenge && active)
        {
            content.Children.Add(new SelectableTextBlock { Text = challenge.UserCode, FontSize = 22 });
            content.Children.Add(Text(challenge.VerificationUri, 12));
            if (ToolkitViewModel.IsSafeLoginUri(challenge.VerificationUri))
                content.Children.Add(ActionButton("Abrir navegador", () => OpenLoginAsync(challenge.VerificationUri)));
            content.Children.Add(ActionButton("Copiar código", async () => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(challenge.UserCode); }));
        }
        if (login?.ErrorCode is { } code) content.Children.Add(Text(ToolkitViewModel.FriendlyError(new ToolkitException(code)), 11));
        content.Children.Add(Text($"Conta: {AccountStatus(account.Status)} · última validação: {FormatDate(account.LastValidatedAt)}", 11, color: "Muted"));
        return content;
    }
}
