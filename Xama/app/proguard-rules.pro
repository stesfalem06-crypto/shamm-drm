# Keep the JNI bridge class/method names intact - Rust looks these up by
# exact name via JNI, so obfuscating them would break the native calls.
-keep class com.shammapps.xama.crypto.NativeCrypto { *; }

# Everything else in the app is fair game for aggressive renaming/shrinking -
# this is part of the anti-tamper hardening: a decompiled build should be as
# unreadable as possible everywhere except the one bridge that must stay stable.
-repackageclasses ''
-allowaccessmodification
-overloadaggressively
