package com.shammapps.xama.data

/** Mirrors XamaMaster/Models/VideoMetadata.cs - the JSON shape must match
 * exactly since Xama reads the same .shammmeta files X Seller pushes. */
data class ShammMeta(
    val VideoId: String = "",
    val OriginalFileName: String = "",
    val Title: String = "",
    val TokenPrice: Int = 0,
    val OriginalSizeBytes: Long = 0,
    val EncryptedAtUtc: String = "",
    val IvBase64: String = "",
    val IsVertical: Boolean = false,
    val WrappedContentKeyForShopsBase64: String = "",
    /** Optional unencrypted poster: base64 JPEG, ~320x180, <= 40 KB. */
    val PosterBase64: String? = null,
    /** null/absent = video; "pdf" = protected PDF (same .shammvid crypto + key flow). */
    val ContentType: String? = null,
)
