using System;
using System.Text.Json.Serialization;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    public sealed class AlpacaResponse<T> {
        [JsonPropertyName("ClientTransactionID")]
        public uint ClientTransactionID { get; set; }

        [JsonPropertyName("ServerTransactionID")]
        public uint ServerTransactionID { get; set; }

        [JsonPropertyName("ErrorNumber")]
        public int ErrorNumber { get; set; }

        [JsonPropertyName("ErrorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("Value")]
        public T? Value { get; set; }
    }

    public sealed class AlpacaResponse {
        [JsonPropertyName("ClientTransactionID")]
        public uint ClientTransactionID { get; set; }

        [JsonPropertyName("ServerTransactionID")]
        public uint ServerTransactionID { get; set; }

        [JsonPropertyName("ErrorNumber")]
        public int ErrorNumber { get; set; }

        [JsonPropertyName("ErrorMessage")]
        public string? ErrorMessage { get; set; }
    }

    public sealed class AlpacaException : Exception {
        public int AlpacaErrorNumber { get; }
        public uint ClientTransactionId { get; }
        public uint ServerTransactionId { get; }

        public AlpacaException(string message, int alpacaErrorNumber, uint clientTx, uint serverTx)
            : base(message) {
            AlpacaErrorNumber = alpacaErrorNumber;
            ClientTransactionId = clientTx;
            ServerTransactionId = serverTx;
        }

        public override string ToString() {
            return $"{base.ToString()} (Alpaca Error={AlpacaErrorNumber}, ClientTx={ClientTransactionId}, ServerTx={ServerTransactionId})";
        }
    }

    public sealed class AlpacaTransportException : Exception {
        public AlpacaTransportException(string message, Exception inner) : base(message, inner) { }
    }
}
