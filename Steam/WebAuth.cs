﻿/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Net;
using System.Text;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class WebAuth : SteamHandler
    {
        public static bool IsAuthorized { get; private set; }

        private static string WebAPIUserNonce;
        private static CookieContainer Cookies;

        static WebAuth()
        {
            Cookies = new CookieContainer();
        }

        public WebAuth(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn));
            manager.Register(new Callback<SteamUser.WebAPIUserNonceCallback>(OnWebAPIUserNonce));
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                return;
            }

            WebAPIUserNonce = callback.WebAPIUserNonce;
        }

        private void OnWebAPIUserNonce(SteamUser.WebAPIUserNonceCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Log.WriteWarn("WebAuth", "Unable to get user nonce: {0}", callback.Result);

                // TODO: Should keep trying?

                return;
            }

            WebAPIUserNonce = callback.Nonce;
        }

        private static bool AuthenticateUser(string nonce)
        {
            // 32 byte random blob of data
            var sessionKey = CryptoHelper.GenerateRandomBlock(32);

            byte[] encryptedSessionKey = null;

            // ... which is then encrypted with RSA using the Steam system's public key
            using (var rsa = new RSACrypto(KeyDictionary.GetPublicKey(Steam.Instance.Client.ConnectedUniverse)))
            {
                encryptedSessionKey = rsa.Encrypt(sessionKey);
            }

            // users hashed loginkey, AES encrypted with the sessionkey
            var encryptedLoginKey = CryptoHelper.SymmetricEncrypt(Encoding.ASCII.GetBytes(nonce), sessionKey);

            using (dynamic userAuth = WebAPI.GetInterface("ISteamUserAuth"))
            {
                KeyValue result;

                try
                {
                    result = userAuth.AuthenticateUser(
                        steamid: Steam.Instance.Client.SteamID.ConvertToUInt64(),
                        sessionkey: WebHelpers.UrlEncode(encryptedSessionKey),
                        encrypted_loginkey: WebHelpers.UrlEncode(encryptedLoginKey),
                        method: "POST",
                        secure: true
                    );
                }
                catch (WebException e)
                {
                    var response = (HttpWebResponse)e.Response;

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        IsAuthorized = false;

                        if (Steam.Instance.Client.IsConnected)
                        {
                            Steam.Instance.User.RequestWebAPIUserNonce();
                        }
                    }

                    Log.WriteWarn("WebAuth", "Failed to authenticate: {0}", e.Message);

                    return false;
                }

                Cookies = new CookieContainer();
                Cookies.Add(new Cookie("steamLogin", result["token"].AsString(), "/", "store.steampowered.com"));
                Cookies.Add(new Cookie("steamLoginSecure", result["tokensecure"].AsString(), "/", "store.steampowered.com"));
            }

            IsAuthorized = true;

            Log.WriteInfo("WebAuth", "Authenticated");

            return true;
        }

        public static HttpWebResponse PerformRequest(string method, string url)
        {
            HttpWebResponse response = null;

            for (var i = 0; i < 5; i++)
            {
                if (!IsAuthorized && !AuthenticateUser(WebAPIUserNonce))
                {
                    continue;
                }

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = 10000;
                request.AllowAutoRedirect = false;
                request.CookieContainer = Cookies;
                request.AutomaticDecompression = DecompressionMethods.GZip;
                request.UserAgent = "Steam Database (https://github.com/SteamDatabase/SteamDatabaseBackend)";

                response = request.GetResponse() as HttpWebResponse;

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect)
                {
                    IsAuthorized = false;

                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new WebException(string.Format("Invalid status code: {0} ({1})", response.StatusCode, (int)response.StatusCode));
                }

                break;
            }

            if (response == null)
            {
                throw new WebException("No data received");
            }

            return response;
        }
    }
}
