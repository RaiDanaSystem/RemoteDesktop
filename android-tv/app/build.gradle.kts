plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.raidana.rdtv"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.raidana.rdtv"
        minSdk = 21
        targetSdk = 34
        // CI passes a monotonically increasing run number so every build can update the previous one.
        val run = System.getenv("GITHUB_RUN_NUMBER")?.toIntOrNull() ?: 0
        versionCode = 100 + run
        versionName = "1.2.$run"
        ndk {
            abiFilters += listOf("armeabi-v7a", "arm64-v8a", "x86_64")
        }
    }

    signingConfigs {
        // One fixed key for every build, so a new APK installs over the old one (no uninstall needed).
        create("release") {
            storeFile = file("../keystore/rdtv-release.jks")
            storePassword = System.getenv("RDTV_STORE_PASSWORD") ?: "rdtv-Sig-2026"
            keyAlias = "rdtv"
            keyPassword = System.getenv("RDTV_KEY_PASSWORD") ?: "rdtv-Sig-2026"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    packaging {
        resources.excludes += setOf("META-INF/*.kotlin_module", "META-INF/DEPENDENCIES", "META-INF/INDEX.LIST")
    }
}

dependencies {
    implementation("io.getstream:stream-webrtc-android:1.3.10")
    implementation("com.microsoft.signalr:signalr:8.0.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.google.code.gson:gson:2.10.1")
    implementation("io.reactivex.rxjava3:rxjava:3.1.8")
    implementation("org.slf4j:slf4j-nop:1.7.36")
}
