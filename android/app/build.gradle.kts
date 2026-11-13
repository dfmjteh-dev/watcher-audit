plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

// Версия агента — одна на все агенты: <AgentVersion> в Directory.Build.props
// (docs/development.md, «Версионирование»). versionCode растёт вместе с ней:
// MAJOR*1_000_000 + MINOR*1_000 + PATCH (0.14.4 → 14004).
val agentVersion: String = run {
    val props = rootDir.resolve("../Directory.Build.props").readText()
    Regex("<AgentVersion>([0-9]+\\.[0-9]+\\.[0-9]+)</AgentVersion>").find(props)?.groupValues?.get(1)
        ?: error("Directory.Build.props: нет <AgentVersion>X.Y.Z</AgentVersion>")
}
val agentVersionCode: Int = agentVersion.split('.').map(String::toInt)
    .let { (major, minor, patch) -> major * 1_000_000 + minor * 1_000 + patch }

// Подпись release — ключ вне репозитория, через переменные окружения
// (docs/android-build.md). Без них assembleRelease собирает неподписанный APK.
val releaseKeystore: String? = System.getenv("WATCHER_ANDROID_KEYSTORE")

android {
    namespace = "com.watcher.agent"
    compileSdk = 35
    buildToolsVersion = "35.0.0"

    defaultConfig {
        applicationId = "com.watcher.agent"
        minSdk = 29
        targetSdk = 35
        versionCode = agentVersionCode
        versionName = agentVersion
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (releaseKeystore != null) {
            create("release") {
                storeFile = file(releaseKeystore)
                storePassword = System.getenv("WATCHER_ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("WATCHER_ANDROID_KEY_ALIAS")
                keyPassword = System.getenv("WATCHER_ANDROID_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (releaseKeystore != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        buildConfig = true
    }

    lint {
        warningsAsErrors = false
        abortOnError = true
    }
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    testImplementation(libs.junit)
    // Реальный org.json только для JVM-тестов (в main он системный, из Android
    // SDK) — см. комментарий в gradle/libs.versions.toml.
    testImplementation(libs.json)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.ext.junit)
}
