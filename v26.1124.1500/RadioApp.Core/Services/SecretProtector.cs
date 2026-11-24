using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RadioApp.Services;

public static class SecretProtector
{
	private const string DpapiPrefix = "dpapi:";
	private const string PlainFallbackPrefix = "b64:";
	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RadioApp.Studio.StreamSecret.v26");

	public static string Protect(string? plainText)
	{
		if (string.IsNullOrEmpty(plainText))
		{
			return string.Empty;
		}
		byte[] raw = Encoding.UTF8.GetBytes(plainText);
		if (OperatingSystem.IsWindows())
		{
			try
			{
				byte[] encrypted = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
				return DpapiPrefix + Convert.ToBase64String(encrypted);
			}
			catch
			{
			}
		}
		return PlainFallbackPrefix + Convert.ToBase64String(raw);
	}

	public static string Unprotect(string? protectedText)
	{
		if (string.IsNullOrEmpty(protectedText))
		{
			return string.Empty;
		}
		try
		{
			if (protectedText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
			{
				byte[] data = Convert.FromBase64String(protectedText.Substring(DpapiPrefix.Length));
				if (OperatingSystem.IsWindows())
				{
					byte[] decrypted = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
					return Encoding.UTF8.GetString(decrypted);
				}
				return string.Empty;
			}
			if (protectedText.StartsWith(PlainFallbackPrefix, StringComparison.Ordinal))
			{
				byte[] data = Convert.FromBase64String(protectedText.Substring(PlainFallbackPrefix.Length));
				return Encoding.UTF8.GetString(data);
			}
			return protectedText;
		}
		catch
		{
			return string.Empty;
		}
	}
}
