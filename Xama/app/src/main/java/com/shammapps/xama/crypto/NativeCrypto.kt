package com.shammapps.xama.crypto

/**
 * Direct JNI bridge to libshamm_crypto.so (built from crypto-core/ in
 * Phase 1). Class and method names/signatures here are matched exactly by
 * the Rust `#[no_mangle] extern "C"` functions - do not rename without
 * checking crypto-core/src/lib.rs, and keep this class exempted in
 * proguard-rules.pro or the native lookup breaks at runtime.
 */
internal object NativeCrypto {
    init {
        System.loadLibrary("shamm_crypto")
    }

    const val KEY_LEN = 32
    const val IV_LEN = 16
    const val WRAPPED_KEY_LEN = 12 + 32 + 16 // nonce + ciphertext + tag

    external fun shamm_derive_device_key(
        fingerprint: ByteArray, fingerprintLen: Int, outKey: ByteArray, outKeyLen: Int
    ): Int

    external fun shamm_unwrap_key(
        wrapped: ByteArray, wrappedLen: Int,
        deviceKey: ByteArray, deviceKeyLen: Int,
        outContentKey: ByteArray, outContentKeyLen: Int
    ): Int

    external fun shamm_ctr_crypt(
        contentKey: ByteArray, contentKeyLen: Int,
        iv: ByteArray, ivLen: Int,
        streamOffset: Long,
        data: ByteArray, dataLen: Int
    ): Int

    external fun shamm_wipe(buf: ByteArray, len: Int): Int
}
