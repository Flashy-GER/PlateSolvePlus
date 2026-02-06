using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface ISecondaryAfStatusPublisher {
        void Publish(SecondaryAutofocusRunState state);
    }

    public sealed class NullSecondaryAfStatusPublisher : ISecondaryAfStatusPublisher {
        public static readonly NullSecondaryAfStatusPublisher Instance = new();
        private NullSecondaryAfStatusPublisher() { }
        public void Publish(SecondaryAutofocusRunState state) { /* noop */ }
    }
}
