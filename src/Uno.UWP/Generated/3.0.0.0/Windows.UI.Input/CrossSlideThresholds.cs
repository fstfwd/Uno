#pragma warning disable 108 // new keyword hiding
#pragma warning disable 114 // new keyword hiding
namespace Windows.UI.Input
{
	#if __ANDROID__ || __IOS__ || NET46 || __WASM__
	[global::Uno.NotImplemented]
	#endif
	public  partial struct CrossSlideThresholds 
	{
		// Forced skipping of method Windows.UI.Input.CrossSlideThresholds.CrossSlideThresholds()
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  float SelectionStart;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  float SpeedBumpStart;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  float SpeedBumpEnd;
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		public  float RearrangeStart;
		#endif
	}
}
