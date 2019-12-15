﻿//-----------------------------------------------------------------------------
// Filename: SIPClient.cs
//
// Description: A SIP client for making and receiving calls. 
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//  
// History:
// 27 Mar 2012	Aaron Clauson	Refactored, Hobart, Australia.
// 03 Dec 2019  Aaron Clauson   Replace separate client and server user agents with full user agent.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;

namespace SIPSorcery.SoftPhone
{
    public class SIPClient : IVoIPClient
    {
        private static string _sdpMimeContentType = SDP.SDP_MIME_CONTENTTYPE;
        private static int TRANSFER_RESPONSE_TIMEOUT_SECONDS = 10;

        private string m_sipUsername = SIPSoftPhoneState.SIPUsername;
        private string m_sipPassword = SIPSoftPhoneState.SIPPassword;
        private string m_sipServer = SIPSoftPhoneState.SIPServer;
        private string m_sipFromName = SIPSoftPhoneState.SIPFromName;

        private SIPTransport m_sipTransport;
        private SIPUserAgent m_userAgent;
        private SIPServerUserAgent m_pendingIncomingCall;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public event Action<SIPClient> CallAnswer;                 // Fires when an outgoing SIP call is answered.
        public event Action<SIPClient> CallEnded;                  // Fires when an incoming or outgoing call is over.
        public event Action<SIPClient> RemotePutOnHold;            // Fires when the remote call party puts us on hold.
        public event Action<SIPClient> RemoteTookOffHold;          // Fires when the remote call party takes us off hold.
        public event Action<SIPClient, string> StatusMessage;      // Fires when the SIP client has a status message it wants to inform the UI about.

        /// <summary>
        /// The RTP timestamp to set on audio packets sent for the RTP session
        /// associated with this client.
        /// </summary>
        public uint AudioTimestamp;

        /// <summary>
        /// The RTP session associated with this client.
        /// </summary>
        public RTPSession RtpSession
        {
            get { return m_userAgent.RtpSession; }
        }

        /// <summary>
        /// Once a call is established this holds the properties of the established SIP dialogue.
        /// </summary>
        public SIPDialogue Dialogue
        {
            get { return m_userAgent.Dialogue; }
        }

        /// <summary>
        /// Returns true of this SIP client is on an active call.
        /// </summary>
        public bool IsCallActive
        {
            get { return m_userAgent.IsCallActive; }
        }

        public SIPClient(SIPTransport sipTransport)
        {
            m_sipTransport = sipTransport;
            m_userAgent = new SIPUserAgent(m_sipTransport, null);
            m_userAgent.ClientCallTrying += CallTrying;
            m_userAgent.ClientCallRinging += CallRinging;
            m_userAgent.ClientCallAnswered += CallAnswered;
            m_userAgent.ClientCallFailed += CallFailed;
            m_userAgent.OnCallHungup += CallFinished;
            m_userAgent.ServerCallCancelled += IncomingCallCancelled;
            m_userAgent.RemotePutOnHold += OnRemotePutOnHold;
            m_userAgent.RemoteTookOffHold += OnRemoteTookOffHold;
        }

        /// <summary>
        /// Places an outgoing SIP call.
        /// </summary>
        /// <param name="destination">The SIP URI to place a call to. The destination can be a full SIP URI in which case the all will
        /// be placed anonymously directly to that URI. Alternatively it can be just the user portion of a URI in which case it will
        /// be sent to the configured SIP server.</param>
        public async void Call(string destination)
        {
            // Determine if this is a direct anonymous call or whether it should be placed using the pre-configured SIP server account. 
            SIPURI callURI = null;
            string sipUsername = null;
            string sipPassword = null;
            string fromHeader = null;

            if (destination.Contains("@") || m_sipServer == null)
            {
                // Anonymous call direct to SIP server specified in the URI.
                callURI = SIPURI.ParseSIPURIRelaxed(destination);
                fromHeader = (new SIPFromHeader(m_sipFromName, SIPURI.ParseSIPURI(SIPFromHeader.DEFAULT_FROM_URI), null)).ToString();
            }
            else
            {
                // This call will use the pre-configured SIP account.
                callURI = SIPURI.ParseSIPURIRelaxed(destination + "@" + m_sipServer);
                sipUsername = m_sipUsername;
                sipPassword = m_sipPassword;
                fromHeader = (new SIPFromHeader(m_sipFromName, new SIPURI(m_sipUsername, m_sipServer, null), null)).ToString();
            }

            StatusMessage(this, $"Starting call to {callURI}.");

            var lookupResult = await Task.Run(() =>
            {
                var result = SIPDNSManager.ResolveSIPService(callURI, false);
                return result;
            });

            if (lookupResult == null || lookupResult.LookupError != null)
            {
                StatusMessage(this, $"Call failed, could not resolve {callURI}.");
            }
            else
            {
                StatusMessage(this, $"Call progressing, resolved {callURI} to {lookupResult.GetSIPEndPoint()}.");
                System.Diagnostics.Debug.WriteLine($"DNS lookup result for {callURI}: {lookupResult.GetSIPEndPoint()}.");
                var dstAddress = lookupResult.GetSIPEndPoint().Address;
                SIPCallDescriptor callDescriptor = new SIPCallDescriptor(sipUsername, sipPassword, callURI.ToString(), fromHeader, null, null, null, null, SIPCallDirection.Out, _sdpMimeContentType, null, null);
                m_userAgent.Call(callDescriptor);
            }
        }

        /// <summary>
        /// Cancels an outgoing SIP call that hasn't yet been answered.
        /// </summary>
        public void Cancel()
        {
            StatusMessage(this, "Cancelling SIP call to " + m_userAgent.CallDescriptor?.Uri + ".");
            m_userAgent.Cancel();
        }

        /// <summary>
        /// Accepts an incoming call. This is the first step in answering a call.
        /// From this point the call can still be rejected, redirected or answered.
        /// </summary>
        /// <param name="sipRequest">The SIP request containing the incoming call request.</param>
        public void Accept(SIPRequest sipRequest)
        {
            m_pendingIncomingCall = m_userAgent.AcceptCall(sipRequest);
        }

        /// <summary>
        /// Answers an incoming SIP call.
        /// </summary>
        public void Answer()
        {
            if (m_pendingIncomingCall == null)
            {
                StatusMessage(this, $"There was no pending call available to answer.");
            }
            else
            {
                m_userAgent.Answer(m_pendingIncomingCall);
                m_pendingIncomingCall = null;
            }
        }

        /// <summary>
        /// Redirects an incoming SIP call.
        /// </summary>
        public void Redirect(string destination)
        {
            m_pendingIncomingCall?.Redirect(SIPResponseStatusCodesEnum.MovedTemporarily, SIPURI.ParseSIPURIRelaxed(destination));
        }

        /// <summary>
        /// Rejects an incoming SIP call.
        /// </summary>
        public void Reject()
        {
            m_pendingIncomingCall?.Reject(SIPResponseStatusCodesEnum.BusyHere, null, null);
        }

        /// <summary>
        /// Hangsup an established SIP call.
        /// </summary>
        public void Hangup()
        {
            if (m_userAgent.IsCallActive)
            {
                m_userAgent.Hangup();
                CallFinished();
            }
        }

        /// <summary>
        /// Sends a request to the remote call party to initiate a blind transfer to the
        /// supplied destination.
        /// </summary>
        /// <param name="destination">The SIP URI of the blind transfer destination.</param>
        /// <returns>True if the transfer was accepted or false if not.</returns>
        public async Task<bool> BlindTransfer(string destination)
        {
            if (SIPURI.TryParse(destination, out var uri))
            {
                return await m_userAgent.BlindTransfer(uri, TimeSpan.FromSeconds(TRANSFER_RESPONSE_TIMEOUT_SECONDS), _cts.Token);
            }
            else
            {
                StatusMessage(this, $"The transfer destination was not a valid SIP URI.");
                return false;
            }
        }

        /// <summary>
        /// Sends a request to the remote call party to initiate an attended transfer.
        /// </summary>
        /// <param name="transferee">The dialog that will be replaced on the initial call party.</param>
        /// <returns>True if the transfer was accepted or false if not.</returns>
        public async Task<bool> AttendedTransfer(SIPDialogue transferee)
        {
            return await m_userAgent.AttendedTransfer(transferee, TimeSpan.FromSeconds(TRANSFER_RESPONSE_TIMEOUT_SECONDS), _cts.Token);
        }

        /// <summary>
        /// Puts the remote call party on hold.
        /// </summary>
        public void PutOnHold()
        {
            m_userAgent.PutOnHold();
            // At this point we could stop listening to the remote party's RTP and play something 
            // else and also stop sending our microphone output and play some music.
            StatusMessage(this, "Remote party put on hold");
        }

        /// <summary>
        /// Takes the remote call party off hold.
        /// </summary>
        public void TakeOffHold()
        {
            m_userAgent.TakeOffHold();
            // At ths point we should reverse whatever changes we made to the media stream when we
            // put the remote call part on hold.
            StatusMessage(this, "Remote taken off on hold");
        }

        /// <summary>
        /// Sends a DTMF event to the remote call party.
        /// </summary>
        /// <param name="key">The key for the event to send. Can only be 0 to 9, * and #.</param>
        public async Task SendDTMF(byte key)
        {
            if (m_userAgent.IsCallActive)
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                var dtmfEvent = new RTPEvent(key, false, RTPEvent.DEFAULT_VOLUME, 1200, RTPSession.DTMF_EVENT_PAYLOAD_ID);
                await m_userAgent.RtpSession.SendDtmfEvent(dtmfEvent, cts);
            }
        }

        /// <summary>
        /// Shuts down the SIP client.
        /// </summary>
        public void Shutdown()
        {
            Hangup();
        }

        /// <summary>
        /// Event handler that notifies us the remote party has put us on hold.
        /// </summary>
        private void OnRemotePutOnHold()
        {
            //_mediaManager.StopSending();
            RemotePutOnHold?.Invoke(this);
        }

        /// <summary>
        /// Event handler that notifies us the remote party has taken us off hold.
        /// </summary>
        private void OnRemoteTookOffHold()
        {
            //_mediaManager.RestartSending();
            RemoteTookOffHold?.Invoke(this);
        }

        /// <summary>
        /// A trying response has been received from the remote SIP UAS on an outgoing call.
        /// </summary>
        private void CallTrying(ISIPClientUserAgent uac, SIPResponse sipResponse)
        {
            StatusMessage(this, "Call trying: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        }

        /// <summary>
        /// A ringing response has been received from the remote SIP UAS on an outgoing call.
        /// </summary>
        private void CallRinging(ISIPClientUserAgent uac, SIPResponse sipResponse)
        {
            StatusMessage(this, "Call ringing: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        }

        /// <summary>
        /// An outgoing call was rejected by the remote SIP UAS on an outgoing call.
        /// </summary>
        private void CallFailed(ISIPClientUserAgent uac, string errorMessage)
        {
            StatusMessage(this, "Call failed: " + errorMessage + ".");
            CallFinished();
        }

        /// <summary>
        /// An outgoing call was successfully answered.
        /// </summary>
        /// <param name="uac">The local SIP user agent client that initiated the call.</param>
        /// <param name="sipResponse">The SIP answer response received from the remote party.</param>
        private void CallAnswered(ISIPClientUserAgent uac, SIPResponse sipResponse)
        {
            StatusMessage(this, "Call answered: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");

            if (sipResponse.StatusCode >= 200 && sipResponse.StatusCode <= 299)
            {
                if (sipResponse.Header.ContentType != _sdpMimeContentType)
                {
                    // Payload not SDP, I don't understand :(.
                    StatusMessage(this, "Call was hungup as the answer response content type was not recognised: " + sipResponse.Header.ContentType + ". :(");
                    Hangup();
                }
                else if (sipResponse.Body.IsNullOrBlank())
                {
                    // They said SDP but didn't give us any :(.
                    StatusMessage(this, "Call was hungup as the answer response had an empty SDP payload. :(");
                    Hangup();
                }
                else
                {
                    CallAnswer(this);
                }
            }
            else
            {
                CallFinished();
            }
        }

        /// <summary>
        /// Cleans up after a SIP call has completely finished.
        /// </summary>
        private void CallFinished()
        {
            m_pendingIncomingCall = null;
            CallEnded(this);
        }

        /// <summary>
        /// An incoming call was cancelled by the caller.
        /// </summary>
        private void IncomingCallCancelled(ISIPServerUserAgent uas)
        {
            //SetText(m_signallingStatus, "incoming call cancelled for: " + uas.CallDestination + ".");
            CallFinished();
        }
    }
}
