package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.security.cert.CertificateException

/**
 * 0.15.0: CA сервера из extra caCert — PEM или base64 DER; сертификат сервера,
 * выданный этим CA, принимается, чужой — нет (системные CA его тоже не знают).
 * Сертификаты — тестовые, только открытая часть.
 */
class ServerTrustTest {

    @Test
    fun parsesPemAndDer() {
        val pem = ServerTrust.parseCa(CA_PEM)
        assertNotNull(pem)
        assertEquals("CN=Test Watcher CA", pem!!.subjectX500Principal.name)
        assertEquals(pem, ServerTrust.parseCa(CA_DER_B64))
        assertEquals(pem, ServerTrust.parseCa(ServerTrust.encode(pem)))
    }

    @Test
    fun rejectsGarbage() {
        assertNull(ServerTrust.parseCa(null))
        assertNull(ServerTrust.parseCa(""))
        assertNull(ServerTrust.parseCa("не сертификат"))
        assertNull(ServerTrust.parseCa("AAAA"))
    }

    @Test
    fun trustsLeafIssuedByServerCa() {
        val ca = ServerTrust.parseCa(CA_PEM)!!
        val leaf = ServerTrust.parseCa(LEAF_PEM)!!
        ServerTrust.trustManager(ca).checkServerTrusted(arrayOf(leaf), "ECDHE_ECDSA")
    }

    @Test(expected = CertificateException::class)
    fun rejectsChainNotIssuedByServerCa() {
        // «CA сервера» — посторонний сертификат: самоподписанный CA через него не проходит.
        val unrelated = ServerTrust.parseCa(LEAF_PEM)!!
        ServerTrust.trustManager(unrelated).checkServerTrusted(arrayOf(ServerTrust.parseCa(CA_PEM)!!), "ECDHE_ECDSA")
    }

    private companion object {
        val CA_PEM = """
-----BEGIN CERTIFICATE-----
MIIBmzCCAUGgAwIBAgIUAf91/cSCRUGJeyy5zgyn54x57lEwCgYIKoZIzj0EAwIw
GjEYMBYGA1UEAwwPVGVzdCBXYXRjaGVyIENBMCAXDTI2MTAyOTA1NDUwMVoYDzIx
MjYxMDA1MDU0NTAxWjAaMRgwFgYDVQQDDA9UZXN0IFdhdGNoZXIgQ0EwWTATBgcq
hkjOPQIBBggqhkjOPQMBBwNCAATBdSH0ACf1MSPAO3e5dteRaLtlPTCiBGdRDGuX
aU2SnX2XBqGNbqWYAaRUiEiiTf4G6GbFxgl/Y1XnuruNm3T1o2MwYTAdBgNVHQ4E
FgQUNairGeWB58xUEBypDJ0amCusjyYwHwYDVR0jBBgwFoAUNairGeWB58xUEByp
DJ0amCusjyYwDwYDVR0TAQH/BAUwAwEB/zAOBgNVHQ8BAf8EBAMCAgQwCgYIKoZI
zj0EAwIDSAAwRQIgSuQQf20SWT+j5dj5hQ2caTd4JIuw6HuaLR5RklW4c1oCIQCI
cZIFMAlfHS9fw2FGhxlGICd/lYBUygTkh6Rd1iKgqw==
-----END CERTIFICATE-----
        """.trimIndent()
        val LEAF_PEM = """
-----BEGIN CERTIFICATE-----
MIIBpjCCAUugAwIBAgIUFUmGhTf74hzJSvR0K7ZaOdqPSe0wCgYIKoZIzj0EAwIw
GjEYMBYGA1UEAwwPVGVzdCBXYXRjaGVyIENBMCAXDTI2MTAyOTA1NDUwMVoYDzIx
MjYxMDA1MDU0NTAxWjAXMRUwEwYDVQQDDAx3YXRjaGVyLnRlc3QwWTATBgcqhkjO
PQIBBggqhkjOPQMBBwNCAATXckJ5aIZxlHYwK0/islOIRfsLltiBKOPoZtks0T8T
AynXZ6z7ciB5i97eABQMXK8KA6R7LcRFI2U5pe3rGlIRo3AwbjAXBgNVHREEEDAO
ggx3YXRjaGVyLnRlc3QwEwYDVR0lBAwwCgYIKwYBBQUHAwEwHQYDVR0OBBYEFNMs
LN4bvIch8FSZZ1951y7BVh0sMB8GA1UdIwQYMBaAFDWoqxnlgefMVBAcqQydGpgr
rI8mMAoGCCqGSM49BAMCA0kAMEYCIQCdMYOJ+VPM4f3w3T2hSjKBDpF0JXnNGrEv
6p0nPB+7GgIhAM85qBc//AKhOKMHx6xganmzwg9vB6JdXhv+igw4ibsQ
-----END CERTIFICATE-----
        """.trimIndent()
        const val CA_DER_B64 = "MIIBmzCCAUGgAwIBAgIUAf91/cSCRUGJeyy5zgyn54x57lEwCgYIKoZIzj0EAwIwGjEYMBYGA1UEAwwPVGVzdCBXYXRjaGVyIENBMCAXDTI2MTAyOTA1NDUwMVoYDzIxMjYxMDA1MDU0NTAxWjAaMRgwFgYDVQQDDA9UZXN0IFdhdGNoZXIgQ0EwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAATBdSH0ACf1MSPAO3e5dteRaLtlPTCiBGdRDGuXaU2SnX2XBqGNbqWYAaRUiEiiTf4G6GbFxgl/Y1XnuruNm3T1o2MwYTAdBgNVHQ4EFgQUNairGeWB58xUEBypDJ0amCusjyYwHwYDVR0jBBgwFoAUNairGeWB58xUEBypDJ0amCusjyYwDwYDVR0TAQH/BAUwAwEB/zAOBgNVHQ8BAf8EBAMCAgQwCgYIKoZIzj0EAwIDSAAwRQIgSuQQf20SWT+j5dj5hQ2caTd4JIuw6HuaLR5RklW4c1oCIQCIcZIFMAlfHS9fw2FGhxlGICd/lYBUygTkh6Rd1iKgqw=="
    }
}
