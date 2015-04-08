﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNet.Authentication.Notifications;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.Framework.Logging;
using Microsoft.IdentityModel.Protocols;

namespace Microsoft.AspNet.Authentication.OpenIdConnect
{
    /// <summary>
    /// A per-request authentication handler for the OpenIdConnectAuthenticationMiddleware.
    /// </summary>
    public class OpenIdConnectAuthenticationHandler : AuthenticationHandler<OpenIdConnectAuthenticationOptions>
    {
        private const string NonceProperty = "N";
        private const string UriSchemeDelimiter = "://";
        private readonly ILogger _logger;
        private OpenIdConnectConfiguration _configuration;

        /// <summary>
        /// Creates a new OpenIdConnectAuthenticationHandler
        /// </summary>
        /// <param name="logger"></param>
        public OpenIdConnectAuthenticationHandler(ILogger logger)
        {
            _logger = logger;
        }

        private string CurrentUri
        {
            get
            {
                return Request.Scheme +
                       UriSchemeDelimiter +
                       Request.Host +
                       Request.PathBase +
                       Request.Path +
                       Request.QueryString;
            }
        }

        protected override void ApplyResponseGrant()
        {
            ApplyResponseGrantAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Handles Signout
        /// </summary>
        /// <returns></returns>
        protected override async Task ApplyResponseGrantAsync()
        {
            var signout = SignOutContext;
            if (signout != null)
            {
                if (_configuration == null && Options.ConfigurationManager != null)
                {
                    _configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
                }

                OpenIdConnectMessage openIdConnectMessage = new OpenIdConnectMessage()
                {
                    IssuerAddress = _configuration == null ? string.Empty : (_configuration.EndSessionEndpoint ?? string.Empty),
                    RequestType = OpenIdConnectRequestType.LogoutRequest,
                };

                // Set End_Session_Endpoint in order:
                // 1. properties.Redirect
                // 2. Options.PostLogoutRedirectUri
                var properties = new AuthenticationProperties(signout.Properties);
                if (!string.IsNullOrEmpty(properties.RedirectUri))
                {
                    openIdConnectMessage.PostLogoutRedirectUri = properties.RedirectUri;
                }
                else if (!string.IsNullOrWhiteSpace(Options.PostLogoutRedirectUri))
                {
                    openIdConnectMessage.PostLogoutRedirectUri = Options.PostLogoutRedirectUri;
                }

                var notification = new RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                {
                    ProtocolMessage = openIdConnectMessage
                };

                await Options.Notifications.RedirectToIdentityProvider(notification);

                if (!notification.HandledResponse)
                {
                    string redirectUri = notification.ProtocolMessage.CreateLogoutRequestUrl();
                    if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                    {
                        _logger.LogWarning(Resources.OIDCH_0051_RedirectUriLogoutIsNotWellFormed, (redirectUri ?? "null"));
                    }

                    Response.Redirect(redirectUri);
                }
            }
        }

        protected override void ApplyResponseChallenge()
        {
            ApplyResponseChallengeAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Responds to a 401 Challenge. Sends an OpenIdConnect message to the 'identity authority' to obtain an identity.
        /// </summary>
        /// <returns></returns>
        /// <remarks>Uses log id's OIDCH-0026 - OIDCH-0050, next num: 37</remarks>
        protected override async Task ApplyResponseChallengeAsync()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(format: Resources.OIDCH_0026_ApplyResponseChallengeAsync, args: this.GetType().ToString());
            }

            if (ShouldConvertChallengeToForbidden())
            {
                _logger.LogDebug(data: Resources.OIDCH_0027_401_ConvertedTo_403);
                Response.StatusCode = 403;
                return;
            }

            if (Response.StatusCode != 401)
            {
                _logger.LogDebug(format: Resources.OIDCH_0028_StatusCodeNot401, args: Response.StatusCode.ToString());
                return;
            }

            // When Automatic should redirect on 401 even if there wasn't an explicit challenge.
            if (ChallengeContext == null && !Options.AutomaticAuthentication)
            {
                _logger.LogDebug(data: Resources.OIDCH_0029_ChallengeContextEqualsNull);
                return;
            }

            // order for redirect_uri
            // 1. challenge.Properties.RedirectUri
            // 2. Options.RedirectUri
            // 3. CurrentUri if Options.DefaultToCurrentUriOnRedirect is true)
            AuthenticationProperties properties;
            if (ChallengeContext == null)
            {
                properties = new AuthenticationProperties();
            }
            else
            {
                properties = new AuthenticationProperties(ChallengeContext.Properties);
            }

            string redirectToUse = null;
            if (!string.IsNullOrWhiteSpace(properties.RedirectUri))
            {
                _logger.LogDebug(format: Resources.OIDCH_0030_Using_Properties_RedirectUri, args: properties.RedirectUri);
                redirectToUse = properties.RedirectUri;
            }
            else if (!string.IsNullOrWhiteSpace(Options.RedirectUri))
            {
                _logger.LogDebug(format: Resources.OIDCH_0031_Using_Options_RedirectUri, args: Options.RedirectUri);
                redirectToUse = Options.RedirectUri;
            }
            else if (Options.DefaultToCurrentUriOnRedirect)
            {
                _logger.LogDebug(format: Resources.OIDCH_0032_UsingCurrentUriRedirectUri, args: CurrentUri);
                redirectToUse = CurrentUri;
            }

            // When redeeming a 'code' for an AccessToken, this value is needed
            if (!string.IsNullOrWhiteSpace(redirectToUse))
            {
                properties.Dictionary.Add(OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey, redirectToUse);
            }

            if (_configuration == null && Options.ConfigurationManager != null)
            {
                _configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
            }

            var message = new OpenIdConnectMessage
            {
                ClientId = Options.ClientId,
                IssuerAddress = _configuration == null ? string.Empty : (_configuration.AuthorizationEndpoint ?? string.Empty),
                RedirectUri = redirectToUse,
                // [brentschmaltz] - this should be a property on RedirectToIdentityProviderNotification not on the OIDCMessage.
                RequestType = OpenIdConnectRequestType.AuthenticationRequest,
                Resource = Options.Resource,
                ResponseMode = Options.ResponseMode,
                ResponseType = Options.ResponseType,
                Scope = Options.Scope,
                State = OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey + "=" + Uri.EscapeDataString(Options.StateDataFormat.Protect(properties))
            };

            if (Options.ProtocolValidator.RequireNonce)
            {
                message.Nonce = Options.ProtocolValidator.GenerateNonce();
                if (Options.NonceCache != null)
                {
                    if (!Options.NonceCache.TryAddNonce(message.Nonce))
                    {
                        _logger.LogError(format: Resources.OIDCH_0033_TryAddNonceFailed, args: message.Nonce);
                        throw new OpenIdConnectProtocolException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0033_TryAddNonceFailed, message.Nonce));
                    }
                }
                else
                {
                    WriteNonceCookie(message.Nonce);
                }
            }

            var redirectToIdentityProviderNotification = new RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
            {
                ProtocolMessage = message
            };

            await Options.Notifications.RedirectToIdentityProvider(redirectToIdentityProviderNotification);
            if (redirectToIdentityProviderNotification.HandledResponse)
            {
                _logger.LogInformation(message: Resources.OIDCH_0034_RedirectToIdentityProviderNotificationHandledResponse);
                return;
            }
            else if (redirectToIdentityProviderNotification.Skipped)
            {
                _logger.LogInformation(message: Resources.OIDCH_0035_RedirectToIdentityProviderNotificationSkipped);
                return;
            }

            string redirectUri = redirectToIdentityProviderNotification.ProtocolMessage.CreateAuthenticationRequestUrl();
            if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            {
                _logger.LogWarning(format: Resources.OIDCH_0036_UriIsNotWellFormed, args: (redirectUri ?? "null"));
            }

            Response.Redirect(redirectUri);
        }

        protected override AuthenticationTicket AuthenticateCore()
        {
            return AuthenticateCoreAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Invoked to process incoming OpenIdConnect messages.
        /// </summary>
        /// <returns>An <see cref="AuthenticationTicket"/> if successful.</returns>
        /// <remarks>Uses log id's OIDCH-0000 - OIDCH-0025</remarks>
        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(format: Resources.OIDCH_0000_AuthenticateCoreAsync, args: this.GetType().ToString());
            }

            // Allow login to be constrained to a specific path. Need to make this runtime configurable.
            if (Options.CallbackPath.HasValue && Options.CallbackPath != (Request.PathBase + Request.Path))
            {
                return null;
            }

            OpenIdConnectMessage message = null;

            // assumption: if the ContentType is "application/x-www-form-urlencoded" it should be safe to read as it is small.
            if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(Request.ContentType)
              // May have media/type; charset=utf-8, allow partial match.
              && Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              && Request.Body.CanRead)
            {
                IFormCollection form = await Request.ReadFormAsync();
                Request.Body.Seek(0, SeekOrigin.Begin);
                message = new OpenIdConnectMessage(form);
            }

            if (message == null)
            {
                return null;
            }

            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(format: Resources.OIDCH_0001_MessageReceived, args: message.BuildRedirectUrl());
                }

                var messageReceivedNotification =
                    new MessageReceivedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                    {
                        ProtocolMessage = message
                    };

                await Options.Notifications.MessageReceived(messageReceivedNotification);
                if (messageReceivedNotification.HandledResponse)
                {
                    _logger.LogInformation(message: Resources.OIDCH_0002_MessageReceivedNotificationHandledResponse);
                    return messageReceivedNotification.AuthenticationTicket;
                }

                if (messageReceivedNotification.Skipped)
                {
                    _logger.LogInformation(message: Resources.OIDCH_0003_MessageReceivedNotificationSkipped);
                    return null;
                }

                // runtime always adds state, if we don't find it OR we failed to 'unprotect' it this is not a message we should process.
                if (string.IsNullOrWhiteSpace(message.State))
                {
                    _logger.LogError(message: Resources.OIDCH_0004_MessageStateIsNullOrWhiteSpace);
                    return null;
                }

                var properties = GetPropertiesFromState(message.State);
                if (properties == null)
                {
                    _logger.LogError(message: Resources.OIDCH_0005_MessageStateIsInvalid);
                    return null;
                }

                // devs will need to hook AuthenticationFailedNotification to avoid having 'raw' runtime errors displayed to users.
                if (!string.IsNullOrWhiteSpace(message.Error))
                {
                   _logger.LogError(format: Resources.OIDCH_0006_MessageErrorNotNull, args: message.Error);
                    throw new OpenIdConnectProtocolException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0006_MessageErrorNotNull, message.Error));
                }

                AuthenticationTicket ticket = null;
                JwtSecurityToken jwt = null;

                if (_configuration == null && Options.ConfigurationManager != null)
                {
                    _logger.LogDebug(data: Resources.OIDCH_0007_UpdatingConfiguration);
                    _configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
                }

                // OpenIdConnect protocol allows a Code to be received without the id_token
                if (!string.IsNullOrWhiteSpace(message.IdToken))
                {
                    _logger.LogDebug(format: Resources.OIDCH_0020_IdTokenReceived, args: message.IdToken);
                    var securityTokenReceivedNotification =
                        new SecurityTokenReceivedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                        {
                            ProtocolMessage = message
                        };

                    await Options.Notifications.SecurityTokenReceived(securityTokenReceivedNotification);
                    if (securityTokenReceivedNotification.HandledResponse)
                    {
                        _logger.LogInformation(message: Resources.OIDCH_0008_SecurityTokenReceivedNotificationHandledResponse);
                        return securityTokenReceivedNotification.AuthenticationTicket;
                    }

                    if (securityTokenReceivedNotification.Skipped)
                    {
                        _logger.LogInformation(message: Resources.OIDCH_0009_SecurityTokenReceivedNotificationSkipped);
                        return null;
                    }

                    // Copy and augment to avoid cross request race conditions for updated configurations.
                    TokenValidationParameters validationParameters = Options.TokenValidationParameters.Clone();
                    if (_configuration != null)
                    {
                        if (string.IsNullOrWhiteSpace(validationParameters.ValidIssuer))
                        {
                            validationParameters.ValidIssuer = _configuration.Issuer;
                        }
                        else if (!string.IsNullOrWhiteSpace(_configuration.Issuer))
                        {
                            validationParameters.ValidIssuers = (validationParameters.ValidIssuers == null ? new[] { _configuration.Issuer } : validationParameters.ValidIssuers.Concat(new[] { _configuration.Issuer }));
                        }

                        validationParameters.IssuerSigningKeys = (validationParameters.IssuerSigningKeys == null ? _configuration.SigningKeys : validationParameters.IssuerSigningKeys.Concat(_configuration.SigningKeys));
                    }

                    SecurityToken validatedToken = null;
                    ClaimsPrincipal principal = null;
                    foreach (var validator in Options.SecurityTokenValidators)
                    {
                        if (validator.CanReadToken(message.IdToken))
                        {
                            principal = validator.ValidateToken(message.IdToken, validationParameters, out validatedToken);
                            jwt = validatedToken as JwtSecurityToken;
                            if (jwt == null)
                            {
                                _logger.LogError(format: Resources.OIDCH_0010_ValidatedSecurityTokenNotJwt, args: (validatedToken == null ? "null" : validatedToken.GetType().ToString()));
                                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0010_ValidatedSecurityTokenNotJwt, (validatedToken == null ? "null" : validatedToken.GetType().ToString())));
                            }
                        }
                    }

                    if (validatedToken == null)
                    {
                        _logger.LogError(format: Resources.OIDCH_0011_UnableToValidateToken, args: message.IdToken);
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0011_UnableToValidateToken, message.IdToken));
                    }

                    ticket = new AuthenticationTicket(principal, properties, Options.AuthenticationScheme);
                    if (!string.IsNullOrWhiteSpace(message.SessionState))
                    {
                        ticket.Properties.Dictionary[OpenIdConnectSessionProperties.SessionState] = message.SessionState;
                    }

                    if (_configuration != null && !string.IsNullOrWhiteSpace(_configuration.CheckSessionIframe))
                    {
                        ticket.Properties.Dictionary[OpenIdConnectSessionProperties.CheckSessionIFrame] = _configuration.CheckSessionIframe;
                    }

                    // Rename?
                    if (Options.UseTokenLifetime)
                    {
                        DateTime issued = validatedToken.ValidFrom;
                        if (issued != DateTime.MinValue)
                        {
                            ticket.Properties.IssuedUtc = issued;
                        }

                        DateTime expires = validatedToken.ValidTo;
                        if (expires != DateTime.MinValue)
                        {
                            ticket.Properties.ExpiresUtc = expires;
                        }
                    }

                    var securityTokenValidatedNotification =
                        new SecurityTokenValidatedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                        {
                            AuthenticationTicket = ticket,
                            ProtocolMessage = message
                        };

                    await Options.Notifications.SecurityTokenValidated(securityTokenValidatedNotification);
                    if (securityTokenValidatedNotification.HandledResponse)
                    {
                        _logger.LogInformation(message: Resources.OIDCH_0012_SecurityTokenValidatedNotificationHandledResponse);
                        return securityTokenValidatedNotification.AuthenticationTicket;
                    }

                    if (securityTokenValidatedNotification.Skipped)
                    {
                        _logger.LogInformation(message: Resources.OIDCH_0013_SecurityTokenValidatedNotificationSkipped);
                        return null;
                    }

                    string nonce = jwt.Payload.Nonce;
                    if (Options.NonceCache != null)
                    {
                        // if the nonce cannot be removed, it was used
                        if (!Options.NonceCache.TryRemoveNonce(nonce))
                        {
                            nonce = null;
                        }
                    }
                    else
                    {
                        nonce = ReadNonceCookie(nonce);
                    }

                    var protocolValidationContext = new OpenIdConnectProtocolValidationContext
                    {
                        AuthorizationCode = message.Code,
                        Nonce = nonce, 
                    };

                    Options.ProtocolValidator.Validate(jwt, protocolValidationContext);
                }

                if (message.Code != null)
                {
                    _logger.LogDebug(format: Resources.OIDCH_0014_CodeReceived, args: message.Code);
                    if (ticket == null)
                    {
                        ticket = new AuthenticationTicket(properties, Options.AuthenticationScheme);
                    }

                    var authorizationCodeReceivedNotification = new AuthorizationCodeReceivedNotification(Context, Options)
                    {
                        AuthenticationTicket = ticket,
                        Code = message.Code,
                        JwtSecurityToken = jwt,
                        ProtocolMessage = message,
                        RedirectUri = ticket.Properties.Dictionary.ContainsKey(OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey) ?
                                      ticket.Properties.Dictionary[OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey] : string.Empty,
                    };

                    await Options.Notifications.AuthorizationCodeReceived(authorizationCodeReceivedNotification);
                    if (authorizationCodeReceivedNotification.HandledResponse)
                    {
                        _logger.LogInformation(message: Resources.OIDCH_0015_CodeReceivedNotificationHandledResponse);
                        return authorizationCodeReceivedNotification.AuthenticationTicket;
                    }

                    if (authorizationCodeReceivedNotification.Skipped)
                    {
                        _logger.LogInformation(message: Resources.OIDCH_0016_CodeReceivedNotificationSkipped);
                        return null;
                    }
                }

                return ticket;
            }
            catch (Exception exception)
            {
                _logger.LogError(message: Resources.OIDCH_0017_ExceptionOccurredWhileProcessingMessage, error: exception);

                // Refresh the configuration for exceptions that may be caused by key rollovers. The user can also request a refresh in the notification.
                if (Options.RefreshOnIssuerKeyNotFound && exception.GetType().Equals(typeof(SecurityTokenSignatureKeyNotFoundException)))
                {
                    Options.ConfigurationManager.RequestRefresh();
                }

                var authenticationFailedNotification =
                    new AuthenticationFailedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                    {
                        ProtocolMessage = message,
                        Exception = exception
                    };

                await Options.Notifications.AuthenticationFailed(authenticationFailedNotification);
                if (authenticationFailedNotification.HandledResponse)
                {
                    _logger.LogInformation(message: Resources.OIDCH_0018_AuthenticationFailedNotificationHandledResponse);
                    return authenticationFailedNotification.AuthenticationTicket;
                }

                if (authenticationFailedNotification.Skipped)
                {
                    _logger.LogInformation(message: Resources.OIDCH_0019_AuthenticationFailedNotificationSkipped);
                    return null;
                }

                throw;
            }
        }

        /// <summary>
        /// Adds the nonce to <see cref="HttpResponse.Cookies"/>.
        /// </summary>
        /// <param name="nonce">the nonce to remember.</param>
        /// <remarks><see cref="HttpResponse.Cookies.Append"/>is called to add a cookie with the name: 'OpenIdConnectAuthenticationDefaults.Nonce + <see cref="OpenIdConnectAuthenticationOptions.StringDataFormat.Protect"/>(nonce)'.
        /// The value of the cookie is: "N".</remarks>
        private void WriteNonceCookie(string nonce)
        {
            if (string.IsNullOrWhiteSpace(nonce))
            {
                throw new ArgumentNullException("nonce");
            }

            Response.Cookies.Append(
                OpenIdConnectAuthenticationDefaults.CookieNoncePrefix + Options.StringDataFormat.Protect(nonce),
                NonceProperty,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps
                });
        }

        /// <summary>
        /// Searches <see cref="HttpRequest.Cookies"/> for a matching nonce.
        /// </summary>
        /// <param name="nonce">the nonce that we are looking for.</param>
        /// <returns>echos 'nonce' if a cookie is found that matches, null otherwise.</returns>
        /// <remarks>Examine <see cref="HttpRequest.Cookies.Keys"/> that start with the prefix: 'OpenIdConnectAuthenticationDefaults.Nonce'. 
        /// <see cref="OpenIdConnectAuthenticationOptions.StringDataFormat.Unprotect"/> is used to obtain the actual 'nonce'. If the nonce is found, then <see cref="HttpResponse.Cookies.Delete"/> is called.</remarks>
        private string ReadNonceCookie(string nonce)
        {
            if (nonce == null)
            {
                return null;
            }

            foreach (var nonceKey in Request.Cookies.Keys)
            {
                if (nonceKey.StartsWith(OpenIdConnectAuthenticationDefaults.CookieNoncePrefix))
                {
                    try
                    {
                        string nonceDecodedValue = Options.StringDataFormat.Unprotect(nonceKey.Substring(OpenIdConnectAuthenticationDefaults.CookieNoncePrefix.Length, nonceKey.Length - OpenIdConnectAuthenticationDefaults.CookieNoncePrefix.Length));
                        if (nonceDecodedValue == nonce)
                        {
                            var cookieOptions = new CookieOptions
                            {
                                HttpOnly = true,
                                Secure = Request.IsHttps
                            };

                            Response.Cookies.Delete(nonceKey, cookieOptions);
                            return nonce;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to un-protect the nonce cookie.", ex);
                    }
                }
            }

            return null;
        }

        private AuthenticationProperties GetPropertiesFromState(string state)
        {
            // assume a well formed query string: <a=b&>OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey=kasjd;fljasldkjflksdj<&c=d>
            int startIndex = 0;
            if (string.IsNullOrWhiteSpace(state) || (startIndex = state.IndexOf(OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey, StringComparison.Ordinal)) == -1)
            {
                return null;
            }

            int authenticationIndex = startIndex + OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey.Length;
            if (authenticationIndex == -1 || authenticationIndex == state.Length || state[authenticationIndex] != '=')
            {
                return null;
            }

            // scan rest of string looking for '&'
            authenticationIndex++;
            int endIndex = state.Substring(authenticationIndex, state.Length - authenticationIndex).IndexOf("&", StringComparison.Ordinal);

            // -1 => no other parameters are after the AuthenticationPropertiesKey
            if (endIndex == -1)
            {
                return Options.StateDataFormat.Unprotect(Uri.UnescapeDataString(state.Substring(authenticationIndex).Replace('+', ' ')));
            }
            else
            {
                return Options.StateDataFormat.Unprotect(Uri.UnescapeDataString(state.Substring(authenticationIndex, endIndex).Replace('+', ' ')));
            }
        }

        /// <summary>
        /// Calls InvokeReplyPathAsync
        /// </summary>
        /// <returns>True if the request was handled, false if the next middleware should be invoked.</returns>
        public override Task<bool> InvokeAsync()
        {
            return InvokeReplyPathAsync();
        }

        private async Task<bool> InvokeReplyPathAsync()
        {
            AuthenticationTicket ticket = await AuthenticateAsync();

            if (ticket != null)
            {
                if (ticket.Principal != null)
                {
                    Request.HttpContext.Response.SignIn(Options.SignInScheme, ticket.Principal, ticket.Properties);
                }

                // Redirect back to the original secured resource, if any.
                if (!string.IsNullOrWhiteSpace(ticket.Properties.RedirectUri))
                {
                    Response.Redirect(ticket.Properties.RedirectUri);
                    return true;
                }
            }

            return false;
        }
    }
}