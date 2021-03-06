#pragma warning disable 108 // new keyword hiding
#pragma warning disable 114 // new keyword hiding
namespace Windows.Devices.PointOfService
{
	#if __ANDROID__ || __IOS__ || NET46 || __WASM__
	#if __ANDROID__ || __IOS__ || NET46 || __WASM__
	[global::Uno.NotImplemented]
	#endif
	public   enum PosPrinterRotation 
	{
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		Normal,
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		Right90,
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		Left90,
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		Rotate180,
		#endif
	}
	#endif
}
