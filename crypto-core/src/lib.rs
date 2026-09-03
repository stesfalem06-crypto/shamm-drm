//! Shamm shared crypto core.
//!
//! One library, compiled twice by GitHub Actions:
//!   - Windows target -> shamm_crypto.dll  -> used by Xama Master & X Seller (C#, via P/Invoke)
//!   - Android targets -> libshamm_crypto.so -> used by Xama (Kotlin, via JNI)
//!
//! Design choices, and why:
//!
//! 1. VIDEO BODY = AES-256-CTR, not AES-GCM.
//!    A video player needs to SEEK (jump to any timestamp) without decrypting
//!    the whole file first. CTR mode supports random-access decryption of any
//!    byte range. GCM does not (it's a single authenticated stream), so it's
//!    wrong for video content even though it's the "safer by default" choice
//!    for most data. We accept the trade-off (CTR alone has no built-in
//!    tamper-detection) because playback integrity here is enforced by the
//!    key-wrapping + device-lock layer below, not by per-byte authentication.
//!
//! 2. THE CONTENT KEY = AES-256-GCM wrapped, once per device.
//!    Each video has ONE random 32-byte content key. That key is small, so
//!    GCM's "must fit in memory, can't seek" limitation doesn't matter here,
//!    and GCM gives us tamper-evidence: if a wrapped key is corrupted or
//!    forged, unwrapping fails loudly instead of silently returning garbage.
//!
//! 3. DEVICE KEY = HKDF-SHA256 over the phone's hardware fingerprint.
//!    X Seller reads a hardware fingerprint from the phone over USB/ADB and
//!    derives a device key from it. Xama re-derives the SAME key locally at
//!    playback time from its own hardware fingerprint. Neither app ever
//!    transmits the device key itself - it's always re-derived, never stored
//!    or sent. A copied video+wrapped-key pair is inert on any other phone
//!    because that phone's fingerprint derives a different key entirely.

use aes::Aes256;
use aes_gcm::{aead::Aead, Aes256Gcm, KeyInit, Nonce};
use ctr::cipher::{KeyIvInit, StreamCipher, StreamCipherSeek};
use hkdf::Hkdf;
use rand::RngCore;
use sha2::Sha256;
use std::slice;
use zeroize::Zeroize;

mod jni_bridge; // Android-only; compiled out entirely on Windows (see the file's #![cfg])

type Aes256CtrBE = ctr::Ctr128BE<Aes256>;

const KEY_LEN: usize = 32; // AES-256
const GCM_NONCE_LEN: usize = 12;
const GCM_TAG_LEN: usize = 16;
const CTR_IV_LEN: usize = 16;

/// Error codes returned across the FFI boundary. Negative = failure.
#[repr(i32)]
enum ShammResult {
    Ok = 0,
    NullPointer = -1,
    BadLength = -2,
    CryptoFailure = -3, // e.g. GCM tag mismatch -> tampered/forged wrapped key
}

/// Generates a fresh random 32-byte content key for one video.
/// Called once by Xama Master when a new video is encrypted.
///
/// out_key: caller-provided buffer, must be exactly 32 bytes.
#[no_mangle]
pub extern "C" fn shamm_generate_key(out_key: *mut u8, out_key_len: u32) -> i32 {
    if out_key.is_null() {
        return ShammResult::NullPointer as i32;
    }
    if out_key_len as usize != KEY_LEN {
        return ShammResult::BadLength as i32;
    }
    let buf = unsafe { slice::from_raw_parts_mut(out_key, KEY_LEN) };
    rand::thread_rng().fill_bytes(buf);
    ShammResult::Ok as i32
}

/// Derives a stable, device-specific 32-byte key from a hardware fingerprint.
/// Both X Seller (at send time) and Xama (at playback time) call this with
/// the SAME fingerprint bytes for the same phone, so they always land on the
/// same device key without ever exchanging it directly.
///
/// fingerprint: e.g. SHA-256(Android ID + hardware serial + ABI), computed
/// by the caller before this function is invoked (see Phase 2/4 for how
/// each platform gathers it).
#[no_mangle]
pub extern "C" fn shamm_derive_device_key(
    fingerprint: *const u8,
    fingerprint_len: u32,
    out_key: *mut u8,
    out_key_len: u32,
) -> i32 {
    if fingerprint.is_null() || out_key.is_null() {
        return ShammResult::NullPointer as i32;
    }
    if fingerprint_len == 0 || out_key_len as usize != KEY_LEN {
        return ShammResult::BadLength as i32;
    }
    let fp = unsafe { slice::from_raw_parts(fingerprint, fingerprint_len as usize) };
    let out = unsafe { slice::from_raw_parts_mut(out_key, KEY_LEN) };

    // Fixed application-level salt (not secret) + the fingerprint as input
    // keying material. "shamm-device-key-v1" scopes this derivation so it
    // can never collide with any other HKDF use in the system.
    let hk = Hkdf::<Sha256>::new(Some(b"shamm-fixed-salt-v1"), fp);
    if hk.expand(b"shamm-device-key-v1", out).is_err() {
        return ShammResult::CryptoFailure as i32;
    }
    ShammResult::Ok as i32
}

/// Wraps (encrypts) a video's content key so only one specific device can
/// open it. Called by X Seller at the moment it sends a video over USB.
///
/// Output layout (all written into out_wrapped, caller must size the buffer
/// to at least 12 + 32 + 16 = 60 bytes):
///   [12-byte GCM nonce][32-byte encrypted content key][16-byte GCM tag]
#[no_mangle]
pub extern "C" fn shamm_wrap_key(
    content_key: *const u8,
    content_key_len: u32,
    device_key: *const u8,
    device_key_len: u32,
    out_wrapped: *mut u8,
    out_wrapped_len: u32,
) -> i32 {
    if content_key.is_null() || device_key.is_null() || out_wrapped.is_null() {
        return ShammResult::NullPointer as i32;
    }
    if content_key_len as usize != KEY_LEN
        || device_key_len as usize != KEY_LEN
        || out_wrapped_len as usize != GCM_NONCE_LEN + KEY_LEN + GCM_TAG_LEN
    {
        return ShammResult::BadLength as i32;
    }

    let ck = unsafe { slice::from_raw_parts(content_key, KEY_LEN) };
    let dk = unsafe { slice::from_raw_parts(device_key, KEY_LEN) };
    let out = unsafe { slice::from_raw_parts_mut(out_wrapped, out_wrapped_len as usize) };

    let mut nonce_bytes = [0u8; GCM_NONCE_LEN];
    rand::thread_rng().fill_bytes(&mut nonce_bytes);

    let cipher = Aes256Gcm::new_from_slice(dk).expect("key is exactly 32 bytes");
    let nonce = Nonce::from_slice(&nonce_bytes);

    match cipher.encrypt(nonce, ck) {
        Ok(ciphertext_and_tag) => {
            out[..GCM_NONCE_LEN].copy_from_slice(&nonce_bytes);
            out[GCM_NONCE_LEN..].copy_from_slice(&ciphertext_and_tag);
            ShammResult::Ok as i32
        }
        Err(_) => ShammResult::CryptoFailure as i32,
    }
}

/// Unwraps a video's content key using this device's own derived key.
/// Called by Xama right before playback starts. If this phone isn't the
/// one the video was sent to, device_key will be wrong and this fails
/// with CryptoFailure (GCM tag won't verify) - the video simply won't play.
#[no_mangle]
pub extern "C" fn shamm_unwrap_key(
    wrapped: *const u8,
    wrapped_len: u32,
    device_key: *const u8,
    device_key_len: u32,
    out_content_key: *mut u8,
    out_content_key_len: u32,
) -> i32 {
    if wrapped.is_null() || device_key.is_null() || out_content_key.is_null() {
        return ShammResult::NullPointer as i32;
    }
    if wrapped_len as usize != GCM_NONCE_LEN + KEY_LEN + GCM_TAG_LEN
        || device_key_len as usize != KEY_LEN
        || out_content_key_len as usize != KEY_LEN
    {
        return ShammResult::BadLength as i32;
    }

    let w = unsafe { slice::from_raw_parts(wrapped, wrapped_len as usize) };
    let dk = unsafe { slice::from_raw_parts(device_key, KEY_LEN) };
    let out = unsafe { slice::from_raw_parts_mut(out_content_key, KEY_LEN) };

    let (nonce_bytes, ct_and_tag) = w.split_at(GCM_NONCE_LEN);
    let cipher = Aes256Gcm::new_from_slice(dk).expect("key is exactly 32 bytes");
    let nonce = Nonce::from_slice(nonce_bytes);

    match cipher.decrypt(nonce, ct_and_tag) {
        Ok(plaintext) => {
            out.copy_from_slice(&plaintext);
            ShammResult::Ok as i32
        }
        Err(_) => ShammResult::CryptoFailure as i32,
    }
}

/// Encrypts or decrypts a chunk of raw video bytes in place using AES-256-CTR.
/// CTR is symmetric (same operation encrypts and decrypts), and seekable:
/// pass the byte offset of this chunk within the overall file via
/// `stream_offset` and the cipher will position itself correctly, so Xama
/// can decrypt only the bytes it's about to play without touching the rest
/// of the file.
///
/// iv: 16 bytes, generated once per video file by Xama Master and stored
/// alongside the encrypted file (it is not secret - CTR security relies on
/// the key, and the key never leaves the wrap/unwrap functions above).
#[no_mangle]
pub extern "C" fn shamm_ctr_crypt(
    content_key: *const u8,
    content_key_len: u32,
    iv: *const u8,
    iv_len: u32,
    stream_offset: u64,
    data: *mut u8,
    data_len: u32,
) -> i32 {
    if content_key.is_null() || iv.is_null() || data.is_null() {
        return ShammResult::NullPointer as i32;
    }
    if content_key_len as usize != KEY_LEN || iv_len as usize != CTR_IV_LEN {
        return ShammResult::BadLength as i32;
    }

    let ck = unsafe { slice::from_raw_parts(content_key, KEY_LEN) };
    let ivb = unsafe { slice::from_raw_parts(iv, CTR_IV_LEN) };
    let buf = unsafe { slice::from_raw_parts_mut(data, data_len as usize) };

    let mut cipher = Aes256CtrBE::new(ck.into(), ivb.into());
    if cipher.try_seek(stream_offset).is_err() {
        return ShammResult::CryptoFailure as i32;
    }
    cipher.apply_keystream(buf);
    ShammResult::Ok as i32
}

/// Wipes a key buffer the caller is about to free/drop. C#/Kotlin should
/// call this on any key byte array as soon as they're done with it, rather
/// than just letting garbage collection get to it eventually.
#[no_mangle]
pub extern "C" fn shamm_wipe(buf: *mut u8, len: u32) -> i32 {
    if buf.is_null() {
        return ShammResult::NullPointer as i32;
    }
    let slice = unsafe { slice::from_raw_parts_mut(buf, len as usize) };
    slice.zeroize();
    ShammResult::Ok as i32
}
