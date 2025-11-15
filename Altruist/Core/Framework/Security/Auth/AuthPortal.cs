using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security
{
    public interface IAuthPortal
    {
        SessionAuthContext OnUpgrade(SessionAuthContext context, string clientId);
        Task OnUpgradeSuccess(SessionAuthContext context, string clientId, IIssue issue);
        Task OnUpgradeFailed(SessionAuthContext context, string clientId);
    }

    public class AuthPortal : Portal, IAuthPortal
    {
        protected readonly IAuthService _authService;
        protected readonly IAltruistRouter _router;
        protected readonly IConnectionManager _connectionManager;

        public AuthPortal(
            IAuthService authService,
            IAltruistRouter router,
            IConnectionManager connectionManager)
        {
            _authService = authService;
            _router = router;
            _connectionManager = connectionManager;
        }

        /// <summary>
        /// Hook called before upgrade, allows modifying the context.
        /// Default: returns the context unchanged.
        /// </summary>
        public virtual SessionAuthContext OnUpgrade(SessionAuthContext context, string clientId)
            => context;

        /// <summary>
        /// Hook called after a successful upgrade (token issued).
        /// Default: no-op.
        /// </summary>
        public virtual Task OnUpgradeSuccess(SessionAuthContext context, string clientId, IIssue issue)
            => Task.CompletedTask;

        /// <summary>
        /// Hook called after a failed upgrade attempt.
        /// Default: no-op.
        /// </summary>
        public virtual Task OnUpgradeFailed(SessionAuthContext context, string clientId)
            => Task.CompletedTask;

        [Gate("upgrade")]
        public async Task UpgradeAuth(SessionAuthContext context, string clientId)
        {
            var connection = await _connectionManager.GetConnectionAsync(clientId);

            try
            {
                context = OnUpgrade(context, clientId);
                var issue = await _authService.Upgrade(context, clientId);

                if (issue != null)
                {
                    await OnUpgradeSuccess(context, clientId, issue);

                    if (issue is IPacketBase packet)
                    {
                        await _router.Client.SendAsync(clientId, packet);
                    }
                }
                else
                {
                    await OnUpgradeFailed(context, clientId);

                    var result = ResultPacket.Failed(
                        code: TransportCode.Unauthorized,
                        reason: "Invalid or expired token"
                    );

                    await _router.Client.SendAsync(clientId, result);
                }
            }
            catch (SecurityTokenExpiredException)
            {
                await OnUpgradeFailed(context, clientId);

                var result = ResultPacket.Failed(
                    code: TransportCode.Unauthorized,
                    reason: "Invalid or expired token"
                );

                await _router.Client.SendAsync(clientId, result);
            }
            catch (Exception)
            {
                await OnUpgradeFailed(context, clientId);

                var result = ResultPacket.Failed(
                    code: TransportCode.InternalServerError,
                    reason: "Authentication upgrade failed due to an internal error."
                );

                await _router.Client.SendAsync(clientId, result);
            }
            finally
            {
                if (connection != null)
                {
                    await connection.CloseOutputAsync();
                    await connection.CloseAsync();
                }
            }
        }
    }
}
