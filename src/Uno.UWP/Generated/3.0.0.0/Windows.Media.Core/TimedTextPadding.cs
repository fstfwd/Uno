#pragma warning disable 108 // new keyword hiding
#pragma warning disable 114 // new keyword hiding
namespace Windows.Media.Core
{
	#if __ANDROID__ || __IOS__ || NET46 || __WASM__
	[global::Uno.NotImplemented]
	#endif
	public  partial struct TimedTextPadding 
	{
		// Forced skipping of method Windows.Media.Core.TimedTextPadding.TimedTextPadding()
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  double Before;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  double After;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  double Start;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  double End;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  global::Windows.Media.Core.TimedTextUnit Unit;
		#endif
	}
}
