plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.shammapps.xama"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.shammapps.xama"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            // Obfuscation + shrinking (part of the "maximum effort" anti-tamper
            // request) - see proguard-rules.pro for the DRM-specific rules.
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }

    // libshamm_crypto.so (built by crypto-core's GitHub Actions job) is
    // dropped into src/main/jniLibs/<abi>/ automatically before this module
    // builds - see .github/workflows/build-xama.yml.
    sourceSets {
        getByName("main") {
            jniLibs.srcDirs("src/main/jniLibs")
        }
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.viewpager2:viewpager2:1.1.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.cardview:cardview:1.0.0")

    // ExoPlayer (via the current Media3 artifact name) - the base for a
    // fast, stable player; we hook our decrypting DataSource into it rather
    // than reimplementing video playback/decoding from scratch.
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-ui:1.4.1")
    implementation("androidx.media3:media3-common:1.4.1")

    // Root/tamper detection library (maintained, widely used, permissive license)
    implementation("com.scottyab:rootbeer-lib:0.1.0")

    implementation("com.google.code.gson:gson:2.11.0")
}
