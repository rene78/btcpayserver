using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Security;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Controllers.GreenField
{
    public class LightningUnavailableExceptionFilter : Attribute, IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            context.Result = new ObjectResult(new GreenfieldAPIError("ligthning-node-unavailable", $"The lightning node is unavailable ({context.Exception.GetType().Name}: {context.Exception.Message})")) { StatusCode = 503 };
            // Do not mark handled, it is possible filters above have better errors
        }
    }
    public abstract class LightningNodeApiController : Controller
    {
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IAuthorizationService _authorizationService;

        protected LightningNodeApiController(BTCPayNetworkProvider btcPayNetworkProvider,
            ISettingsRepository settingsRepository,
            IAuthorizationService authorizationService)
        {
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _settingsRepository = settingsRepository;
            _authorizationService = authorizationService;
        }

        public virtual async Task<IActionResult> GetInfo(string cryptoCode)
        {
            var lightningClient = await GetLightningClient(cryptoCode, true);
            var info = await lightningClient.GetInfo();
            return Ok(new LightningNodeInformationData()
            {
                BlockHeight = info.BlockHeight,
                NodeURIs = info.NodeInfoList.Select(nodeInfo => nodeInfo).ToArray()
            });
        }

        public virtual async Task<IActionResult> ConnectToNode(string cryptoCode, ConnectToNodeRequest request)
        {
            var lightningClient = await GetLightningClient(cryptoCode, true);
            if (request?.NodeURI is null)
            {
                ModelState.AddModelError(nameof(request.NodeURI), "A valid node info was not provided to connect to");
            }

            if (!ModelState.IsValid)
            {
                return this.CreateValidationError(ModelState);
            }

            var result = await lightningClient.ConnectTo(request.NodeURI);
            switch (result)
            {
                case ConnectionResult.Ok:
                    return Ok();
                case ConnectionResult.CouldNotConnect:
                    return this.CreateAPIError("could-not-connect", "Could not connect to the remote node");
            }

            return Ok();
        }

        public virtual async Task<IActionResult> GetChannels(string cryptoCode)
        {
            var lightningClient = await GetLightningClient(cryptoCode, true);

            var channels = await lightningClient.ListChannels();
            return Ok(channels.Select(channel => new LightningChannelData()
            {
                Capacity = channel.Capacity,
                ChannelPoint = channel.ChannelPoint.ToString(),
                IsActive = channel.IsActive,
                IsPublic = channel.IsPublic,
                LocalBalance = channel.LocalBalance,
                RemoteNode = channel.RemoteNode.ToString()
            }));
        }


        public virtual async Task<IActionResult> OpenChannel(string cryptoCode, OpenLightningChannelRequest request)
        {
            var lightningClient = await GetLightningClient(cryptoCode, true);
            if (lightningClient == null)
            {
                return NotFound();
            }

            if (request?.NodeURI is null)
            {
                ModelState.AddModelError(nameof(request.NodeURI),
                    "A valid node info was not provided to open a channel with");
            }

            if (request.ChannelAmount == null)
            {
                ModelState.AddModelError(nameof(request.ChannelAmount), "ChannelAmount is missing");
            }
            else if (request.ChannelAmount.Satoshi <= 0)
            {
                ModelState.AddModelError(nameof(request.ChannelAmount), "ChannelAmount must be more than 0");
            }

            if (request.FeeRate == null)
            {
                ModelState.AddModelError(nameof(request.FeeRate), "FeeRate is missing");
            }
            else if (request.FeeRate.SatoshiPerByte <= 0)
            {
                ModelState.AddModelError(nameof(request.FeeRate), "FeeRate must be more than 0");
            }

            if (!ModelState.IsValid)
            {
                return this.CreateValidationError(ModelState);
            }

            var response = await lightningClient.OpenChannel(new Lightning.OpenChannelRequest()
            {
                ChannelAmount = request.ChannelAmount,
                FeeRate = request.FeeRate,
                NodeInfo = request.NodeURI
            });

            string errorCode, errorMessage;
            switch (response.Result)
            {
                case OpenChannelResult.Ok:
                    return Ok();
                case OpenChannelResult.AlreadyExists:
                    errorCode = "channel-already-exists";
                    errorMessage = "The channel already exists";
                    break;
                case OpenChannelResult.CannotAffordFunding:
                    errorCode = "cannot-afford-funding";
                    errorMessage = "Not enough money to open a channel";
                    break;
                case OpenChannelResult.NeedMoreConf:
                    errorCode = "need-more-confirmations";
                    errorMessage = "Need to wait for more confirmations";
                    break;
                case OpenChannelResult.PeerNotConnected:
                    errorCode = "peer-not-connected";
                    errorMessage = "Not connected to peer";
                    break;
                default:
                    throw new NotSupportedException("Unknown OpenChannelResult");
            }
            return this.CreateAPIError(errorCode, errorMessage);
        }

        public virtual async Task<IActionResult> GetDepositAddress(string cryptoCode)
        {
            var lightningClient = await GetLightningClient(cryptoCode, true);
            if (lightningClient == null)
            {
                return NotFound();
            }

            return Ok(new JValue((await lightningClient.GetDepositAddress()).ToString()));
        }

        public virtual async Task<IActionResult> PayInvoice(string cryptoCode, PayLightningInvoiceRequest lightningInvoice)
        {
            var lightningClient = await GetLightningClient(cryptoCode, true);
            var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);

            if (lightningInvoice?.BOLT11 is null ||
                !BOLT11PaymentRequest.TryParse(lightningInvoice.BOLT11, out _, network.NBitcoinNetwork))
            {
                ModelState.AddModelError(nameof(lightningInvoice.BOLT11), "The BOLT11 invoice was invalid.");
            }

            if (!ModelState.IsValid)
            {
                return this.CreateValidationError(ModelState);
            }

            var result = await lightningClient.Pay(lightningInvoice.BOLT11);
            switch (result.Result)
            {
                case PayResult.CouldNotFindRoute:
                    return this.CreateAPIError("could-not-find-route", "Impossible to find a route to the peer");
                case PayResult.Error:
                    return this.CreateAPIError("generic-error", result.ErrorDetail);
                case PayResult.Ok:
                    return Ok();
                default:
                    throw new NotSupportedException("Unsupported Payresult");
            }
        }

        public virtual async Task<IActionResult> GetInvoice(string cryptoCode, string id)
        {
            var lightningClient = await GetLightningClient(cryptoCode, false);

            if (lightningClient == null)
            {
                return NotFound();
            }

            var inv = await lightningClient.GetInvoice(id);
            if (inv == null)
            {
                return this.CreateAPIError(404, "invoice-not-found", "Impossible to find a lightning invoice with this id");
            }
            return Ok(ToModel(inv));
        }

        public virtual async Task<IActionResult> CreateInvoice(string cryptoCode, CreateLightningInvoiceRequest request)
        {
            var lightningClient = await GetLightningClient(cryptoCode, false);
            if (request.Amount < LightMoney.Zero)
            {
                ModelState.AddModelError(nameof(request.Amount), "Amount should be more or equals to 0");
            }

            if (request.Expiry <= TimeSpan.Zero)
            {
                ModelState.AddModelError(nameof(request.Expiry), "Expiry should be more than 0");
            }

            if (!ModelState.IsValid)
            {
                return this.CreateValidationError(ModelState);
            }

            try
            {
                var invoice = await lightningClient.CreateInvoice(
                    new CreateInvoiceParams(request.Amount, request.Description, request.Expiry)
                    {
                        PrivateRouteHints = request.PrivateRouteHints
                    },
                    CancellationToken.None);
                return Ok(ToModel(invoice));
            }
            catch (Exception ex)
            {
                return this.CreateAPIError("generic-error", ex.Message);
            }
        }

        protected JsonHttpException ErrorLightningNodeNotConfiguredForStore()
        {
            return new JsonHttpException(this.CreateAPIError(404, "lightning-not-configured", "The lightning node is not set up"));
        }
        protected JsonHttpException ErrorInternalLightningNodeNotConfigured()
        {
            return new JsonHttpException(this.CreateAPIError(404, "lightning-not-configured", "The internal lightning node is not set up"));
        }
        protected JsonHttpException ErrorCryptoCodeNotFound()
        {
            return new JsonHttpException(this.CreateAPIError(404, "unknown-cryptocode", "This crypto code isn't set up in this BTCPay Server instance"));
        }
        protected JsonHttpException ErrorShouldBeAdminForInternalNode()
        {
            return new JsonHttpException(this.CreateAPIError(403, "missing-permission", "The user should be admin to use the internal lightning node"));
        }

        private LightningInvoiceData ToModel(LightningInvoice invoice)
        {
            return new LightningInvoiceData()
            {
                Amount = invoice.Amount,
                Id = invoice.Id,
                Status = invoice.Status,
                AmountReceived = invoice.AmountReceived,
                PaidAt = invoice.PaidAt,
                BOLT11 = invoice.BOLT11,
                ExpiresAt = invoice.ExpiresAt
            };
        }

        protected async Task<bool> CanUseInternalLightning(bool doingAdminThings)
        {
            
            return (!doingAdminThings && (await _settingsRepository.GetPolicies()).AllowLightningInternalNodeForAll) ||
                (await _authorizationService.AuthorizeAsync(User, null,
                    new PolicyRequirement(Policies.CanUseInternalLightningNode))).Succeeded;
        }

        protected abstract Task<ILightningClient> GetLightningClient(string cryptoCode, bool doingAdminThings);
    }
}
