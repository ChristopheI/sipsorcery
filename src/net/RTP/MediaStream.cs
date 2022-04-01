﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace SIPSorcery.net.RTP
{

    public class AudioStream : MediaStream
    {
        private static ILogger logger = Log.Logger;

        /// <summary>
        /// Gets fired when the remote SDP is received and the set of common audio formats is set.
        /// </summary>
        public event Action<List<AudioFormat>> OnAudioFormatsNegotiated;

        public void CheckAudioFormatsNegotiation()
        {
            if(LocalTrack != null &&
                        LocalTrack.Capabilities.Where(x => x.Name().ToLower() != SDP.TELEPHONE_EVENT_ATTRIBUTE).Count() > 0)
            {
                OnAudioFormatsNegotiated?.Invoke(
                            LocalTrack.Capabilities
                            .Where(x => x.Name().ToLower() != SDP.TELEPHONE_EVENT_ATTRIBUTE)
                            .Select(x => x.ToAudioFormat()).ToList());
            }
        }

        public AudioStream(RtpSessionConfig config) : base(config)
        {
            MediaType = SDPMediaTypesEnum.audio;
        }

        public Boolean RtpEventInProgress { get; set; } = false;

        /// <summary>
        /// Indicates whether this session is using audio.
        /// </summary>
        public bool HasAudio
        {
            get
            {
                return LocalTrack != null && LocalTrack.StreamStatus != MediaStreamStatusEnum.Inactive
                  && RemoteTrack != null && RemoteTrack.StreamStatus != MediaStreamStatusEnum.Inactive;
            }
        }
    }

    public class VideoStream: MediaStream
    {
        private static ILogger logger = Log.Logger;

        /// <summary>
        /// Gets fired when the remote SDP is received and the set of common video formats is set.
        /// </summary>
        public event Action<List<VideoFormat>> OnVideoFormatsNegotiated;

        /// <summary>
        /// Gets fired when a full video frame is reconstructed from one or more RTP packets
        /// received from the remote party.
        /// </summary>
        /// <remarks>
        ///  - Received from end point,
        ///  - The frame timestamp,
        ///  - The encoded video frame payload.
        ///  - The video format of the encoded frame.
        /// </remarks>
        public event Action<IPEndPoint, uint, byte[], VideoFormat> OnVideoFrameReceived;

        public RtpVideoFramer RtpVideoFramer;

        /// <summary>
        /// Indicates whether this session is using video.
        /// </summary>
        public bool HasVideo
        {
            get
            {
                // TODO - CI - need to use dictionnary
                return LocalTrack != null && LocalTrack.StreamStatus != MediaStreamStatusEnum.Inactive
                  && RemoteTrack != null && RemoteTrack.StreamStatus != MediaStreamStatusEnum.Inactive;
            }
        }

        public void CheckVideoFormatsNegotiation()
        {
            if (LocalTrack != null && LocalTrack.Capabilities?.Count() > 0)
            {
                OnVideoFormatsNegotiated?.Invoke(
                            LocalTrack.Capabilities
                            .Select(x => x.ToVideoFormat()).ToList());
            }
        }

        public void ProcessVideoRtpFrame(IPEndPoint endpoint, RTPPacket packet, SDPAudioVideoMediaFormat format)
        {
            if (OnVideoFrameReceived == null)
            {
                return;
            }

            if (RtpVideoFramer != null)
            {
                var frame = RtpVideoFramer.GotRtpPacket(packet);
                if (frame != null)
                {
                    OnVideoFrameReceived?.Invoke(endpoint, packet.Header.Timestamp, frame, format.ToVideoFormat());
                }
            }
            else
            {
                if (format.ToVideoFormat().Codec == VideoCodecsEnum.VP8 ||
                    format.ToVideoFormat().Codec == VideoCodecsEnum.H264)
                {
                    logger.LogDebug($"Video depacketisation codec set to {format.ToVideoFormat().Codec} for SSRC {packet.Header.SyncSource}.");

                    RtpVideoFramer = new RtpVideoFramer(format.ToVideoFormat().Codec);

                    var frame = RtpVideoFramer.GotRtpPacket(packet);
                    if (frame != null)
                    {
                        OnVideoFrameReceived?.Invoke(endpoint, packet.Header.Timestamp, frame, format.ToVideoFormat());
                    }
                }
                else
                {
                    logger.LogWarning($"Video depacketisation logic for codec {format.Name()} has not been implemented, PR's welcome!");
                }
            }
        }

        public VideoStream(RtpSessionConfig config) : base(config)
        {
            MediaType = SDPMediaTypesEnum.video;
        }
    }


    public class MediaStream
    {
        private static ILogger logger = Log.Logger;

        private uint m_lastRtpTimestamp;

        private RtpSessionConfig RtpSessionConfig;
        private Boolean IsSecure;
        private Boolean UseSdpCryptoNegotiation;

        private SecureContext SecureContext;
        private RTPReorderBuffer RTPReorderBuffer = null;

    #region EVENTS

        /// <summary>
        /// Fires when the connection for a media type is classified as timed out due to not
        /// receiving any RTP or RTCP packets within the given period.
        /// </summary>
        public event Action<uint, SDPMediaTypesEnum> OnTimeout;

        /// <summary>
        /// Gets fired when an RTP packet is received from a remote party.
        /// Parameters are:
        ///  - Remote endpoint packet was received from,
        ///  - The media type the packet contains, will be audio or video,
        ///  - The full RTP packet.
        /// </summary>
        public event Action<IPEndPoint, RTPPacket> OnRtpPacketReceived;  // TODO - CI - 

        /// <summary>
        /// Gets fired when an RTP event is detected on the remote call party's RTP stream.
        /// </summary>
        public event Action<IPEndPoint, RTPEvent, RTPHeader> OnRtpEvent;  // TODO - CI - 

        /// <summary>
        /// Gets fired when an RTCP report is received. This event is for diagnostics only.
        /// </summary>
        public event Action<IPEndPoint, RTCPCompoundPacket> OnReceiveReport;  // TODO - CI - 

        /// <summary>
        /// Gets fired when an RTCP report is sent. This event is for diagnostics only.
        /// </summary>
        public event Action<RTCPCompoundPacket> OnSendReport;  // TODO - CI - 


        public event Action<int, IPEndPoint, byte[]> OnRTPDataReceived;
        public event Action<int, IPEndPoint, byte[]> OnRTPControlDataReceived;
        /// <summary>
        /// Event handler for the RTP channel closure.
        /// </summary>
        public event Action<string> OnRTPChannelClosed;

    #endregion EVENTS

    #region PROPERTIES

        protected RTPChannel rtpChannel = null;

        /// <summary>
        /// Indicates whether the session has been closed. Once a session is closed it cannot
        /// be restarted.
        /// </summary>
        public bool IsClosed { get; set; } = false;

        /// <summary>
        /// In order to detect RTP events from the remote party this property needs to 
        /// be set to the payload ID they are using.
        /// </summary>
        public int RemoteRtpEventPayloadID { get; set; } = RTPSession.DEFAULT_DTMF_EVENT_PAYLOAD_ID;

        /// <summary>
        /// To type of this media
        /// </summary>
        public SDPMediaTypesEnum MediaType { get; set; }

        /// <summary>
        /// The local track. Will be null if we are not sending this media.
        /// </summary>
        public MediaStreamTrack LocalTrack { get; set; }

        /// <summary>
        /// The remote video track. Will be null if the remote party is not sending this media
        /// </summary>
        public MediaStreamTrack RemoteTrack { get; set; }

        /// <summary>
        /// The reporting session for this media stream.
        /// </summary>
        public RTCPSession RtcpSession { get; set; }

        /// <summary>
        /// The remote RTP end point this stream is sending media to.
        /// </summary>
        public IPEndPoint DestinationEndPoint { get; set; }

        /// <summary>
        /// The remote RTP control end point this stream is sending to RTCP reports for the media stream to.
        /// </summary>
        public IPEndPoint ControlDestinationEndPoint { get; set; }

    #endregion PROPERTIES


    #region REORDER BUFFER

        public void AddBuffer(TimeSpan dropPacketTimeout)
        {
            RTPReorderBuffer = new RTPReorderBuffer(dropPacketTimeout);
        }

        public void RemoveBuffer(TimeSpan dropPacketTimeout)
        {
            RTPReorderBuffer = null;
        }

        public Boolean UseBuffer()
        {
            return RTPReorderBuffer != null;
        }

        public RTPReorderBuffer GetBuffer()
        {
            return RTPReorderBuffer;
        }

    #endregion REORDER BUFFER


    #region SECURITY CONTEXT

        public void SetSecurityContext(
            ProtectRtpPacket protectRtp,
            ProtectRtpPacket unprotectRtp,
            ProtectRtpPacket protectRtcp,
            ProtectRtpPacket unprotectRtcp)
        {
            if (SecureContext != null)
            {
                logger.LogTrace($"Tried adding new SecureContext for media type {MediaType}, but one already existed");
            }

            SecureContext = new SecureContext(protectRtp, unprotectRtp, protectRtcp, unprotectRtcp);
        }

        public SecureContext GetSecurityContext()
        {
            return SecureContext;
        }

        public Boolean IsSecurityContextReady()
        {
            return (SecureContext != null);
        }

        private (bool, byte[]) UnprotectBuffer(byte[] buffer)
        {
            var secureContext = GetSecurityContext();
            if (secureContext != null)
            {
                int res = secureContext.UnprotectRtpPacket(buffer, buffer.Length, out int outBufLen);

                if (res == 0)
                {
                    return (true, buffer.Take(outBufLen).ToArray());
                }
                else
                {
                    logger.LogWarning($"SRTP unprotect failed for {MediaType}, result {res}.");
                }
            }
            return (false, buffer);
        }

        public bool EnsureBufferUnprotected(byte[] buf, RTPHeader header, out RTPPacket packet)
        {
            if (IsSecure || UseSdpCryptoNegotiation)
            {
                var (succeeded, newBuffer) = UnprotectBuffer(buf);
                if (!succeeded)
                {
                    packet = null;
                    return false;
                }
                packet = new RTPPacket(newBuffer);
            }
            else
            {
                packet = new RTPPacket(buf);
            }
            packet.Header.ReceivedTime = header.ReceivedTime;
            return true;
        }

    #endregion SECURITY CONTEXT


    #region RTP CHANNEL

        public void AddRtpChannel(RTPChannel rtpChannel)
        {
            this.rtpChannel = rtpChannel;
        }

        public Boolean HasRtpChannel()
        {
            return rtpChannel != null;
        }

        public RTPChannel GetRTPChannel()
        {
            return rtpChannel;
        }

    #endregion RTP CHANNEL


        public MediaStream(RtpSessionConfig config)
        {
            IsSecure = config.RtpSecureMediaOption == RtpSecureMediaOptionEnum.DtlsSrtp;
            UseSdpCryptoNegotiation = config.RtpSecureMediaOption == RtpSecureMediaOptionEnum.SdpCryptoNegotiation;
            RtpSessionConfig = config;
        }

        /// <summary>
        /// Creates a new RTCP session for a media track belonging to this RTP session.
        /// </summary>
        /// <param name="mediaType">The media type to create the RTP session for. Must be
        /// audio or video.</param>
        /// <returns>A new RTCPSession object. The RTCPSession must have its Start method called
        /// in order to commence sending RTCP reports.</returns>
        public Boolean CreateRtcpSession()
        {
            if (RtcpSession == null)
            {
                RtcpSession = new RTCPSession(MediaType, 0);
                RtcpSession.OnTimeout += (ssrc, mt) => OnTimeout?.Invoke(ssrc, mt);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Sets the remote end points for a media type supported by this RTP session.
        /// </summary>
        /// <param name="mediaType">The media type, must be audio or video, to set the remote end point for.</param>
        /// <param name="rtpEndPoint">The remote end point for RTP packets corresponding to the media type.</param>
        /// <param name="rtcpEndPoint">The remote end point for RTCP packets corresponding to the media type.</param>
        public void SetDestination(IPEndPoint rtpEndPoint, IPEndPoint rtcpEndPoint)
        {
            DestinationEndPoint = rtpEndPoint;
            ControlDestinationEndPoint = rtcpEndPoint;
        }

        /// <summary>
        /// Attempts to get the highest priority sending format for the remote call party.
        /// </summary>
        /// <returns>The first compatible media format found for the specified media type.</returns>
        public SDPAudioVideoMediaFormat GetSendingFormat()
        {
            if (LocalTrack != null || RemoteTrack != null)
            {
                if (LocalTrack == null)
                {
                    return RemoteTrack.Capabilities.First();
                }
                else if (RemoteTrack == null)
                {
                    return LocalTrack.Capabilities.First();
                }

                SDPAudioVideoMediaFormat format;
                if (MediaType == SDPMediaTypesEnum.audio)
                {

                    format = SDPAudioVideoMediaFormat.GetCompatibleFormats(LocalTrack.Capabilities, RemoteTrack.Capabilities)
                        .Where(x => x.ID != RemoteRtpEventPayloadID).FirstOrDefault();
                }
                else
                {
                    format = SDPAudioVideoMediaFormat.GetCompatibleFormats(LocalTrack.Capabilities, RemoteTrack.Capabilities).First();
                }

                if (format.IsEmpty())
                {
                    // It's not expected that this occurs as a compatibility check is done when the remote session description
                    // is set. By this point a compatible codec should be available.
                    throw new ApplicationException($"No compatible sending format could be found for media {MediaType}.");
                }
                else
                {
                    return format;
                }
            }
            else
            {
                throw new ApplicationException($"Cannot get the {MediaType} sending format, missing either local or remote {MediaType} track.");
            }
        }

        public void ProcessHeaderExtensions(RTPHeader header)
        {
            header.GetHeaderExtensions().ToList().ForEach(x =>
            {
                var ntpTimestamp = x.GetNtpTimestamp(RemoteTrack.HeaderExtensions);
                if (ntpTimestamp.HasValue)
                {
                    RemoteTrack.LastAbsoluteCaptureTimestamp = new TimestampPair() { NtpTimestamp = ntpTimestamp.Value, RtpTimestamp = header.Timestamp };
                }
            });
        }

    }
}
