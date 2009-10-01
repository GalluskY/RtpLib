using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace RtpLib
{
    /// <summary>
    /// Class is used to queue up and order RTP packets
    /// </summary>
    public class RtpStream : Stream
    {

        public const int AutoFlushBufferMaximum = RtpListener.Constants.BufferSize * 15;

        private RtpListener _rtpListener;
        private readonly object _dataLock = new object();
        
        #region constructors

        public RtpStream(IPEndPoint localEp)
        {
            _rtpListener = new RtpListener(localEp);
            _rtpListener.PacketReceived += OnPacketReceived;
            this.AutoFlush = true;
        }

        public RtpStream(int port)
        {
            _rtpListener = new RtpListener(port);
            _rtpListener.PacketReceived += OnPacketReceived;
            this.AutoFlush = true;
        }
        

        #endregion


        void OnPacketReceived(object sender, EventArgs<RtpPacket> e)
        {
            lock (_dataLock)
            {
                _length += e.Data.PayloadLength;

                //Let the EnsureBufferOf know that we got some more data
                Monitor.Pulse(_dataLock);
            }
        }


        #region static methods
        public static RtpStream Open(string uri)
        {
            var test = new Regex(@"(?<proto>[a-zA-Z]+)://@(?<ip>[\d\.]+)?(:(?<port>\d+))?");
            int port;
            IPAddress ip;

            Assert.That(test.IsMatch(uri), () => new ArgumentException("Please use a format of 'udp://@MCIP:PORT' where MCIP is a valid multicast IP address.", "uri"));

            var m = test.Match(uri);

            Assert.AreEqual(m.Groups["proto"].Value.ToLower(), "udp", "protocol");

            if (!IPAddress.TryParse(m.Groups["ip"].Value, out ip))
                ip = IPAddress.Any;

            if (!int.TryParse(m.Groups["port"].Value, out port))
                port = 1234;


            var client = new RtpStream(port);
            client._rtpListener.StartListening();
            client._rtpListener.JoinMulticastGroup(ip);
            return client;

        }
        #endregion

        #region properties



        private long _length;
        public override long Length
        {
            get
            {
                lock (_dataLock)
                {
                    return _length;
                }
            }
        }

        private long _lastFlushPosition;
        private int _bufferPosition;
        /// <summary>
        /// When overridden in a derived class, gets or sets the position within the current stream.
        /// </summary>
        /// <value></value>
        /// <returns>The current position within the stream.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">the position is set to a location prior to last flush position or after the end of the stream. </exception>
        public override long Position
        {
            get
            {
                lock (_dataLock)
                {
                    return _lastFlushPosition + _bufferPosition;
                }
            }
            set
            {
                lock (_dataLock)
                {
                    Assert.IsGreaterThan(value, "position", _lastFlushPosition);
                    Assert.IsLessThan(value, "position", _data.Length);
                    _bufferPosition = (int)(value - _lastFlushPosition);
                }
            }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public bool AutoFlush { get; set; }

        #endregion



        #region public methods


        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_dataLock)
            {
                RunAutoFlush();
                EnsureBufferOf(count);

                Buffer.BlockCopy(_data, _bufferPosition, buffer, offset, count);
                _bufferPosition += count;

                return count;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("The method or operation is not supported.");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("The method or operation is not supported.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("The method or operation is not supported.");
        }

        /// <summary>
        /// Clears all buffers for this stream and updates position and length accordingly
        /// </summary>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs. </exception>
        public override void Flush()
        {
            lock (_dataLock)
            {
                _lastFlushPosition = this.Position;
                var newBuffer = new byte[_data.Length - _bufferPosition];
                Buffer.BlockCopy(_data, _bufferPosition, newBuffer, 0, newBuffer.Length);
                _bufferPosition = 0;

                _data = newBuffer;
            }
        }

        #endregion

        #region private methods

        /// <summary>
        /// Internal method to run the auto flush if required.
        /// </summary>
        private void RunAutoFlush()
        {
            lock (_dataLock)
            {
                if (this.AutoFlush && _data.Length > AutoFlushBufferMaximum)
                    this.Flush();
            }
        }

        private byte[] _data = new byte[] { };

        /// <summary>
        /// Ensures the buffer of a specified [size]. Throws a [TimeoutException] when it takes longer then 1000ms to fill the buffer.
        /// </summary>
        /// <param name="size">The size.</param>
        private void EnsureBufferOf(long size)
        {
            lock (_dataLock)
            {
                while (_data.Length - _bufferPosition < size)
                {
                    var payload = this._rtpListener.GetPayload();

                    if (payload != null)
                    {
                        _data = Concat(_data, payload);
                    }
                    else
                    {
                        //TODO: We should really wait the full amount and use signaling to resume
                        //sleep doesn't release the lock so we need to use wait
                        //otherwise we'll get a ton of backed up jobs in the queue all waiting to update _length
                        //I could be missing an error somewhere that this is causing now, but it seems to have solved my problem
                        Monitor.Wait(_dataLock);
                    }
                }
            }
        }

        public static byte[] Concat(byte[] array1, byte[] array2)
        {
            var newData = new byte[array1.Length + array2.Length];
            Buffer.BlockCopy(array1, 0, newData, 0, array1.Length);
            Buffer.BlockCopy(array2, 0, newData, array1.Length, array2.Length);
            return newData;
        }

        #endregion

        

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                this._rtpListener.Dispose();
            }

            this._rtpListener = null;
           

            lock (_dataLock)
            {
                if(_data != null)
                    _data = null;
            }
        }
    }
}