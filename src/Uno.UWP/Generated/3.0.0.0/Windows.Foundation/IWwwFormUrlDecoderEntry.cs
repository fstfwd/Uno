#pragma warning disable 108 // new keyword hiding
#pragma warning disable 114 // new keyword hiding
namespace Windows.Foundation
{
	#if __ANDROID__ || __IOS__ || NET46 || __WASM__
	[global::Uno.NotImplemented]
	#endif
	public  partial interface IWwwFormUrlDecoderEntry 
	{
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		string Name
		{
			get;
		}
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		string Value
		{
			get;
		}
		#endif
		// Forced skipping of method Windows.Foundation.IWwwFormUrlDecoderEntry.Name.get
		// Forced skipping of method Windows.Foundation.IWwwFormUrlDecoderEntry.Value.get
	}
}
