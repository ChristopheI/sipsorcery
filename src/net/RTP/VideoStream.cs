﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace SIPSorcery.net.RTP
{
    public class VideoStream : MediaStream
    {
        private static ILogger logger = Log.Logger;

        private RtpVideoFramer RtpVideoFramer;

    #region EVENTS

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

    #endregion EVENTS

    #region PROPERTIES

        /// <summary>
        /// Indicates whether this session is using video.
        /// </summary>
        public bool HasVideo
        {
            get
            {
                return LocalTrack != null && LocalTrack.StreamStatus != MediaStreamStatusEnum.Inactive
                  && RemoteTrack != null && RemoteTrack.StreamStatus != MediaStreamStatusEnum.Inactive;
            }
        }

    #endregion PROPERTIES

    #region SEND PACKET

        /// <summary>
        /// Helper method to send a low quality JPEG image over RTP. This method supports a very abbreviated version of RFC 2435 "RTP Payload Format for JPEG-compressed Video".
        /// It's intended as a quick convenient way to send something like a test pattern image over an RTSP connection. More than likely it won't be suitable when a high
        /// quality image is required since the header used in this method does not support quantization tables.
        /// </summary>
        /// <param name="jpegBytes">The raw encoded bytes of the JPEG image to transmit.</param>
        /// <param name="jpegQuality">The encoder quality of the JPEG image.</param>
        /// <param name="jpegWidth">The width of the JPEG image.</param>
        /// <param name="jpegHeight">The height of the JPEG image.</param>
        /// <param name="framesPerSecond">The rate at which the JPEG frames are being transmitted at. used to calculate the timestamp.</param>
        public void SendJpegFrame(uint duration, int payloadTypeID, byte[] jpegBytes, int jpegQuality, int jpegWidth, int jpegHeight)
        {
            if (IsClosed || DestinationEndPoint == null)
            {
                return;
            }
            try
            {
                var videoTrack = LocalTrack;

                if (videoTrack == null)
                {
                    logger.LogWarning("SendJpegFrame was called on an RTP session without a video stream.");
                }
                else if (!HasVideo || videoTrack.StreamStatus == MediaStreamStatusEnum.RecvOnly)
                {
                    return;
                }
                else
                {
                    for (int index = 0; index * RTPSession.RTP_MAX_PAYLOAD < jpegBytes.Length; index++)
                    {
                        uint offset = Convert.ToUInt32(index * RTPSession.RTP_MAX_PAYLOAD);
                        int payloadLength = ((index + 1) * RTPSession.RTP_MAX_PAYLOAD < jpegBytes.Length) ? RTPSession.RTP_MAX_PAYLOAD : jpegBytes.Length - index * RTPSession.RTP_MAX_PAYLOAD;
                        byte[] jpegHeader = RtpVideoFramer.CreateLowQualityRtpJpegHeader(offset, jpegQuality, jpegWidth, jpegHeight);

                        List<byte> packetPayload = new List<byte>();
                        packetPayload.AddRange(jpegHeader);
                        packetPayload.AddRange(jpegBytes.Skip(index * RTPSession.RTP_MAX_PAYLOAD).Take(payloadLength));

                        int markerBit = ((index + 1) * RTPSession.RTP_MAX_PAYLOAD < jpegBytes.Length) ? 0 : 1;

                        var protectRtpPacket = SecureContext?.ProtectRtpPacket;
                        SendRtpPacket(rtpChannel, DestinationEndPoint, packetPayload.ToArray(), videoTrack.Timestamp, markerBit, payloadTypeID, videoTrack.Ssrc, videoTrack.GetNextSeqNum(), RtcpSession, protectRtpPacket);
                    }

                    videoTrack.Timestamp += duration;
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendJpegFrame. " + sockExcp.Message);
            }
        }

        /// <summary>
        /// Sends a H264 frame, represented by an Access Unit, to the remote party.
        /// </summary>
        /// <param name="duration">The duration in timestamp units of the payload (e.g. 3000 for 30fps).</param>
        /// <param name="payloadTypeID">The payload type ID  being used for H264 and that will be set on the RTP header.</param>
        /// <param name="accessUnit">The encoded H264 access unit to transmit. An access unit can contain one or more
        /// NAL's.</param>
        /// <remarks>
        /// An Access Unit can contain one or more NAL's. The NAL's have to be parsed in order to be able to package 
        /// in RTP packets.
        /// 
        /// See https://www.itu.int/rec/dologin_pub.asp?lang=e&id=T-REC-H.264-201602-S!!PDF-E&type=items Annex B for byte stream specification.
        /// </remarks>
        public void SendH264Frame(uint duration, int payloadTypeID, byte[] accessUnit)
        {
            var dstEndPoint = DestinationEndPoint;

            if (IsClosed || dstEndPoint == null || accessUnit == null || accessUnit.Length == 0)
            {
                return;
            }

            if (LocalTrack == null)
            {
                logger.LogWarning("SendH264Frame was called on an RTP session without a video stream.");
            }
            else if (!HasVideo || LocalTrack.StreamStatus == MediaStreamStatusEnum.RecvOnly)
            {
                return;
            }
            else
            {
                foreach (var nal in H264Packetiser.ParseNals(accessUnit))
                {
                    SendH264Nal(duration, payloadTypeID, nal.NAL, nal.IsLast, dstEndPoint, LocalTrack);
                }
            }
        }

        /// <summary>
        /// Sends a single H264 NAL to the remote party.
        /// </summary>
        /// <param name="duration">The duration in timestamp units of the payload (e.g. 3000 for 30fps).</param>
        /// <param name="payloadTypeID">The payload type ID  being used for H264 and that will be set on the RTP header.</param>
        /// <param name="nal">The buffer containing the NAL to send.</param>
        /// <param name="isLastNal">Should be set for the last NAL in the H264 access unit. Determines when the markbit gets set 
        /// and the timestamp incremented.</param>
        /// <param name="dstEndPoint">The destination end point to send to.</param>
        /// <param name="videoTrack">The video track to send on.</param>
        private void SendH264Nal(uint duration, int payloadTypeID, byte[] nal, bool isLastNal, IPEndPoint dstEndPoint, MediaStreamTrack videoTrack)
        {
            //logger.LogDebug($"Send NAL {nal.Length}, is last {isLastNal}, timestamp {videoTrack.Timestamp}.");
            //logger.LogDebug($"nri {nalNri:X2}, type {nalType:X2}.");

            byte nal0 = nal[0];

            if (nal.Length <= RTPSession.RTP_MAX_PAYLOAD)
            {
                // Send as Single-Time Aggregation Packet (STAP-A).
                byte[] payload = new byte[nal.Length];
                int markerBit = isLastNal ? 1 : 0;   // There is only ever one packet in a STAP-A.
                Buffer.BlockCopy(nal, 0, payload, 0, nal.Length);

                var protectRtpPacket = SecureContext?.ProtectRtpPacket;
                SendRtpPacket(rtpChannel, dstEndPoint, payload, videoTrack.Timestamp, markerBit, payloadTypeID, videoTrack.Ssrc, videoTrack.GetNextSeqNum(), RtcpSession, protectRtpPacket);
                //logger.LogDebug($"send H264 {videoChannel.RTPLocalEndPoint}->{dstEndPoint} timestamp {videoTrack.Timestamp}, payload length {payload.Length}, seqnum {videoTrack.SeqNum}, marker {markerBit}.");
                //logger.LogDebug($"send H264 {videoChannel.RTPLocalEndPoint}->{dstEndPoint} timestamp {videoTrack.Timestamp}, STAP-A {h264RtpHdr.HexStr()}, payload length {payload.Length}, seqnum {videoTrack.SeqNum}, marker {markerBit}.");
            }
            else
            {
                nal = nal.Skip(1).ToArray();

                // Send as Fragmentation Unit A (FU-A):
                for (int index = 0; index * RTPSession.RTP_MAX_PAYLOAD < nal.Length; index++)
                {
                    int offset = index * RTPSession.RTP_MAX_PAYLOAD;
                    int payloadLength = ((index + 1) * RTPSession.RTP_MAX_PAYLOAD < nal.Length) ? RTPSession.RTP_MAX_PAYLOAD : nal.Length - index * RTPSession.RTP_MAX_PAYLOAD;

                    bool isFirstPacket = index == 0;
                    bool isFinalPacket = (index + 1) * RTPSession.RTP_MAX_PAYLOAD >= nal.Length;
                    int markerBit = (isLastNal && isFinalPacket) ? 1 : 0;

                    byte[] h264RtpHdr = H264Packetiser.GetH264RtpHeader(nal0, isFirstPacket, isFinalPacket);

                    byte[] payload = new byte[payloadLength + h264RtpHdr.Length];
                    Buffer.BlockCopy(h264RtpHdr, 0, payload, 0, h264RtpHdr.Length);
                    Buffer.BlockCopy(nal, offset, payload, h264RtpHdr.Length, payloadLength);


                    var protectRtpPacket = SecureContext?.ProtectRtpPacket;
                    SendRtpPacket(rtpChannel, dstEndPoint, payload, videoTrack.Timestamp, markerBit, payloadTypeID, videoTrack.Ssrc, videoTrack.GetNextSeqNum(), RtcpSession, protectRtpPacket);
                    //logger.LogDebug($"send H264 {videoChannel.RTPLocalEndPoint}->{dstEndPoint} timestamp {videoTrack.Timestamp}, FU-A {h264RtpHdr.HexStr()}, payload length {payloadLength}, seqnum {videoTrack.SeqNum}, marker {markerBit}.");
                }
            }

            if (isLastNal)
            {
                videoTrack.Timestamp += duration;
            }
        }

        /// <summary>
        /// Sends a VP8 frame as one or more RTP packets.
        /// </summary>
        /// <param name="timestamp">The timestamp to place in the RTP header. Needs
        /// to be based on a 90Khz clock.</param>
        /// <param name="payloadTypeID">The payload ID to place in the RTP header.</param>
        /// <param name="buffer">The VP8 encoded payload.</param>
        public void SendVp8Frame(uint duration, int payloadTypeID, byte[] buffer)
        {
            if (IsClosed || DestinationEndPoint == null)
            {
                return;
            }

            try
            {
                if (LocalTrack == null)
                {
                    logger.LogWarning("SendVp8Frame was called on an RTP session without a video stream.");
                }
                else if (!HasVideo || LocalTrack.StreamStatus == MediaStreamStatusEnum.RecvOnly)
                {
                    return;
                }
                else
                {
                    for (int index = 0; index * RTPSession.RTP_MAX_PAYLOAD < buffer.Length; index++)
                    {
                        int offset = index * RTPSession.RTP_MAX_PAYLOAD;
                        int payloadLength = (offset + RTPSession.RTP_MAX_PAYLOAD < buffer.Length) ? RTPSession.RTP_MAX_PAYLOAD : buffer.Length - offset;

                        byte[] vp8HeaderBytes = (index == 0) ? new byte[] { 0x10 } : new byte[] { 0x00 };
                        byte[] payload = new byte[payloadLength + vp8HeaderBytes.Length];
                        Buffer.BlockCopy(vp8HeaderBytes, 0, payload, 0, vp8HeaderBytes.Length);
                        Buffer.BlockCopy(buffer, offset, payload, vp8HeaderBytes.Length, payloadLength);

                        int markerBit = ((offset + payloadLength) >= buffer.Length) ? 1 : 0; // Set marker bit for the last packet in the frame.

                        var protectRtpPacket = SecureContext?.ProtectRtpPacket;
                        SendRtpPacket(rtpChannel, DestinationEndPoint, payload, LocalTrack.Timestamp, markerBit, payloadTypeID, LocalTrack.Ssrc, LocalTrack.GetNextSeqNum(), RtcpSession, protectRtpPacket);
                        //logger.LogDebug($"send VP8 {videoChannel.RTPLocalEndPoint}->{dstEndPoint} timestamp {videoTrack.Timestamp}, sample length {buffer.Length}.");
                    }

                    LocalTrack.Timestamp += duration;
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendVp8Frame. " + sockExcp.Message);
            }
        }

        /// <summary>
        /// Sends a video sample to the remote peer.
        /// </summary>
        /// <param name="durationRtpUnits">The duration in RTP timestamp units of the video sample. This
        /// value is added to the previous RTP timestamp when building the RTP header.</param>
        /// <param name="sample">The video sample to set as the RTP packet payload.</param>
        public void SendVideo(uint durationRtpUnits, byte[] sample)
        {
            if (DestinationEndPoint != null || (DestinationEndPoint != null) &&
                ((!IsSecure && !UseSdpCryptoNegotiation) || IsSecurityContextReady()))
            {
                var videoSendingFormat = GetSendingFormat();

                switch (videoSendingFormat.Name())
                {
                    case "VP8":
                        int vp8PayloadID = Convert.ToInt32(LocalTrack.Capabilities.First(x => x.Name() == "VP8").ID);
                        SendVp8Frame(durationRtpUnits, vp8PayloadID, sample);
                        break;
                    case "H264":
                        int h264PayloadID = Convert.ToInt32(LocalTrack.Capabilities.First(x => x.Name() == "H264").ID);
                        SendH264Frame(durationRtpUnits, h264PayloadID, sample);
                        break;
                    default:
                        throw new ApplicationException($"Unsupported video format selected {videoSendingFormat.Name()}.");
                }
            }
        }

    #endregion SEND PACKET

    #region RECEIVE PACKET

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

        public void OnReceiveRTPPacket(RTPHeader hdr, SDPAudioVideoMediaFormat format, int localPort, IPEndPoint remoteEndPoint, byte[] buffer)
        {
            base.OnReceiveRTPPacket(hdr, format, localPort, remoteEndPoint, buffer, this);
        }

    #endregion RECEIVE PACKET

        public void CheckVideoFormatsNegotiation()
        {
            if (LocalTrack != null && LocalTrack.Capabilities?.Count() > 0)
            {
                OnVideoFormatsNegotiated?.Invoke(
                            LocalTrack.Capabilities
                            .Select(x => x.ToVideoFormat()).ToList());
            }
        }

        public VideoStream(RtpSessionConfig config) : base(config)
        {
            MediaType = SDPMediaTypesEnum.video;
        }

        
    }

}
