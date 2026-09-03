//! Android-only JNI glue. Windows never compiles this file (see the
//! `cfg(target_os = "android")` dependency gate in Cargo.toml).
//!
//! Why this exists: the functions in lib.rs use the plain C calling
//! convention so C# can call them directly via P/Invoke. The JVM on
//! Android can't call plain C functions that way - it needs each native
//! method registered with a `(JNIEnv, jbyteArray, ...) -> jint` signature.
//! Rather than duplicate every function with JNI-flavored types, this file
//! registers thin adapters at library load time (`JNI_OnLoad`) that unpack
//! the Java arguments and call straight into the same lib.rs functions
//! Windows uses - so the actual crypto logic still exists in exactly one
//! place.

#![cfg(target_os = "android")]

use jni::objects::{JByteArray, JClass};
use jni::sys::{jint, jlong, JNI_VERSION_1_6};
use jni::{JNIEnv, JavaVM};
use std::os::raw::c_void;

use crate::{shamm_ctr_crypt, shamm_derive_device_key, shamm_unwrap_key, shamm_wipe};

/// Copies a Java byte[] into a Rust Vec<u8>.
fn to_vec(env: &mut JNIEnv, arr: &JByteArray) -> Vec<u8> {
    let len = env.get_array_length(arr).unwrap_or(0) as usize;
    let mut buf = vec![0i8; len];
    let _ = env.get_byte_array_region(arr, 0, &mut buf);
    buf.into_iter().map(|b| b as u8).collect()
}

/// Copies a Rust byte slice back into an existing Java byte[] (Kotlin
/// pre-allocates the output arrays, matching the same pattern used on the
/// C# side).
fn write_back(env: &mut JNIEnv, arr: &JByteArray, data: &[u8]) {
    let as_i8: Vec<i8> = data.iter().map(|b| *b as i8).collect();
    let _ = env.set_byte_array_region(arr, 0, &as_i8);
}

#[no_mangle]
pub extern "system" fn Java_com_shammapps_xama_crypto_NativeCrypto_shamm_1derive_1device_1key(
    mut env: JNIEnv, _class: JClass,
    fingerprint: JByteArray, fingerprint_len: jint,
    out_key: JByteArray, out_key_len: jint,
) -> jint {
    let fp = to_vec(&mut env, &fingerprint);
    let mut out = vec![0u8; out_key_len as usize];
    let rc = shamm_derive_device_key(fp.as_ptr(), fingerprint_len as u32, out.as_mut_ptr(), out_key_len as u32);
    write_back(&mut env, &out_key, &out);
    rc
}

#[no_mangle]
pub extern "system" fn Java_com_shammapps_xama_crypto_NativeCrypto_shamm_1unwrap_1key(
    mut env: JNIEnv, _class: JClass,
    wrapped: JByteArray, wrapped_len: jint,
    device_key: JByteArray, device_key_len: jint,
    out_content_key: JByteArray, out_content_key_len: jint,
) -> jint {
    let w = to_vec(&mut env, &wrapped);
    let dk = to_vec(&mut env, &device_key);
    let mut out = vec![0u8; out_content_key_len as usize];
    let rc = shamm_unwrap_key(
        w.as_ptr(), wrapped_len as u32,
        dk.as_ptr(), device_key_len as u32,
        out.as_mut_ptr(), out_content_key_len as u32,
    );
    write_back(&mut env, &out_content_key, &out);
    rc
}

#[no_mangle]
pub extern "system" fn Java_com_shammapps_xama_crypto_NativeCrypto_shamm_1ctr_1crypt(
    mut env: JNIEnv, _class: JClass,
    content_key: JByteArray, content_key_len: jint,
    iv: JByteArray, iv_len: jint,
    stream_offset: jlong,
    data: JByteArray, data_len: jint,
) -> jint {
    let ck = to_vec(&mut env, &content_key);
    let ivb = to_vec(&mut env, &iv);
    let mut buf = to_vec(&mut env, &data);
    let rc = shamm_ctr_crypt(
        ck.as_ptr(), content_key_len as u32,
        ivb.as_ptr(), iv_len as u32,
        stream_offset as u64,
        buf.as_mut_ptr(), data_len as u32,
    );
    write_back(&mut env, &data, &buf);
    rc
}

#[no_mangle]
pub extern "system" fn Java_com_shammapps_xama_crypto_NativeCrypto_shamm_1wipe(
    mut env: JNIEnv, _class: JClass,
    buf: JByteArray, len: jint,
) -> jint {
    let mut v = to_vec(&mut env, &buf);
    let rc = shamm_wipe(v.as_mut_ptr(), len as u32);
    write_back(&mut env, &buf, &v);
    rc
}

/// Called automatically by the JVM the moment System.loadLibrary("shamm_crypto")
/// runs. We don't need to do manual RegisterNatives here because the
/// Java_com_shammapps_... symbol names above follow JNI's standard naming
/// convention, which the JVM resolves automatically - JNI_OnLoad just needs
/// to report which JNI version we support.
#[no_mangle]
pub extern "system" fn JNI_OnLoad(_vm: JavaVM, _reserved: *mut c_void) -> jint {
    JNI_VERSION_1_6
}
