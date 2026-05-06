// 
// Copyright (c) 2026 LeenQa. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
//

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.X509;

public static class MasterListExtractor
{
    public static List<X509Certificate> ExtractAllCertificatesFromMasterList(byte[] mlBytes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<X509Certificate>();
        var parser = new X509CertificateParser();

        void AddCert(X509Certificate cert)
        {
            if (cert == null) return;
            var key = cert.SubjectDN + "|" + cert.SerialNumber;
            if (seen.Add(key))
                results.Add(cert);
        }

        bool TryParseCert(Asn1Object obj, out X509Certificate? certOut)
        {
            certOut = null;
            if (obj is Asn1Sequence)
            {
                try
                {
                    var raw = obj.GetEncoded("DER");
                    var cert = parser.ReadCertificate(raw);
                    if (cert != null)
                    {
                        certOut = cert;
                        return true;
                    }
                }
                catch { /* not a cert */ }
            }
            return false;
        }

        void Walk(Asn1Object obj)
        {
            if (obj == null) return;

            if (TryParseCert(obj, out var c))
            {
                AddCert(c!);
                return;
            }

            switch (obj)
            {
                case Asn1Sequence seq:
                    foreach (Asn1Encodable e in seq)
                        Walk(e.ToAsn1Object());
                    break;

                case Asn1Set set:
                    foreach (Asn1Encodable e in set)
                        Walk(e.ToAsn1Object());
                    break;

                case Asn1OctetString oct:
                    try
                    {
                        var inner = Asn1Object.FromByteArray(oct.GetOctets());
                        Walk(inner);
                    }
                    catch { /* ignore */ }
                    break;

                default:
                    break;
            }
        }

        var top = Asn1Object.FromByteArray(mlBytes);
        var ci = ContentInfo.GetInstance(top);
        var sd = SignedData.GetInstance(ci.Content);

        try
        {
            var certSet = sd.Certificates;
            if (certSet != null)
            {
                foreach (Asn1Encodable enc in certSet)
                {
                    var obj = enc.ToAsn1Object();
                    if (TryParseCert(obj, out var cert))
                        AddCert(cert!);
                    else
                        Walk(obj);
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            var eci = sd.EncapContentInfo;
            var content = eci?.Content; 
            if (content != null)
            {
                var eciObj = content.ToAsn1Object();
                if (eciObj is Asn1OctetString oct)
                {
                    var inner = Asn1Object.FromByteArray(oct.GetOctets());
                    Walk(inner);
                }
                else
                {
                    Walk(eciObj);
                }
            }
        }
        catch { /* ignore */ }

        Walk(sd.ToAsn1Object());

        return results
            .GroupBy(cer => $"{cer.SubjectDN}|{cer.SerialNumber}")
            .Select(g => g.First())
            .ToList();
    }
}
