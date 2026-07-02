@Library('bnp-shared@main') _

pipeline {
    agent any

    triggers {
        // Local PoC: poll SCM every ~2 min. Requires no public inbound URL — Jenkins
        // opens the outbound connection to github.com. 'H' spreads the trigger to avoid
        // spikes. The real webhook path (githubPush + smee.io/ngrok) is documented but
        // not executed here.
        pollSCM('H/2 * * * *')
    }

    environment {
        DOTNET_ROOT          = '/usr/share/dotnet'
        BASE_VERSION         = '1.0.0'
        ARTIFACT_VERSION     = "${BASE_VERSION}-build.${BUILD_NUMBER}"
        ARTIFACT_NAME        = "BnpPoc.Api-${ARTIFACT_VERSION}.zip"
        CHECKSUM_NAME        = "${ARTIFACT_NAME}.sha256"
        NEXUS_URL            = 'http://nexus:8081/repository/dotnet-artifacts'
        SONAR_PROJECT_KEY    = 'BnpPoc.Api'
        OWASP_CVSS_THRESHOLD = '7'
    }

    stages {
        stage('Restore') {
            steps {
                sh 'dotnet restore src/BnpPoc.sln'
            }
        }

        stage('Database Migration') {
            steps {
                withCredentials([usernamePassword(
                    credentialsId: 'sqlserver-credentials',
                    usernameVariable: 'DB_USER',
                    passwordVariable: 'DB_PASS'
                )]) {
                    sh '''#!/bin/bash
                        set -euo pipefail

                        db_ready=false
                        for i in $(seq 1 30); do
                            if (echo > /dev/tcp/sqlserver/1433) >/dev/null 2>&1; then
                                db_ready=true
                                break
                            fi
                            echo "Waiting for sqlserver (attempt $i/30)..."
                            sleep 2
                        done

                        if [ "$db_ready" != "true" ]; then
                            echo "ERROR: sqlserver:1433 never became reachable after 30 attempts." >&2
                            exit 1
                        fi

                        liquibase execute-sql \
                            --url="jdbc:sqlserver://sqlserver:1433;encrypt=true;trustServerCertificate=true" \
                            --username=${DB_USER} \
                            --password=${DB_PASS} \
                            --sql="IF DB_ID('BnpPocDb') IS NULL EXEC('CREATE DATABASE BnpPocDb');"

                        liquibase update \
                            --changelog-file=db/changelog/db.changelog-master.xml \
                            --url="jdbc:sqlserver://sqlserver:1433;databaseName=BnpPocDb;encrypt=true;trustServerCertificate=true" \
                            --username=${DB_USER} \
                            --password=${DB_PASS}
                    '''
                }
            }
        }

        stage('SonarQube Analysis') {
            // sonarscanner BEGIN must precede dotnet build so MSBuild hooks capture
            // Roslyn diagnostics. Build and Test are nested inside this stage
            // intentionally — this is the correct dotnet-sonarscanner pattern.
            steps {
                withSonarQubeEnv('SonarQube') {
                    sh """
                        dotnet-sonarscanner begin \\
                            /k:"\${SONAR_PROJECT_KEY}" \\
                            /d:sonar.host.url="\${SONAR_HOST_URL}" \\
                            /d:sonar.token="\${SONAR_AUTH_TOKEN}" \\
                            /d:sonar.coverageReportPaths="sonarqubecoverage/SonarQube.xml"

                        dotnet build src/BnpPoc.sln --no-restore -c Release

                        dotnet test src/BnpPoc.sln -c Release --no-build \\
                            --collect:"XPlat Code Coverage" \\
                            --results-directory ./TestResults

                        reportgenerator \\
                            "-reports:TestResults/**/coverage.cobertura.xml" \\
                            -targetdir:sonarqubecoverage \\
                            -reporttypes:SonarQube

                        dotnet-sonarscanner end \\
                            /d:sonar.token="\${SONAR_AUTH_TOKEN}"
                    """
                }
            }
        }

        stage('SonarQube Quality Gate') {
            steps {
                timeout(time: 5, unit: 'MINUTES') {
                    waitForQualityGate abortPipeline: true
                }
            }
        }

        stage('OWASP Dependency-Check') {
            steps {
                sh 'mkdir -p dependency-check-report'
                dependencyCheck(
                    additionalArguments: """
                        --scan src/
                        --format HTML
                        --format JSON
                        --out dependency-check-report
                        --failOnCVSS ${OWASP_CVSS_THRESHOLD}
                    """,
                    odcInstallation: 'dependency-check'
                )
            }
            post {
                always {
                    dependencyCheckPublisher(
                        pattern: 'dependency-check-report/dependency-check-report.json'
                    )
                    archiveArtifacts(
                        artifacts: 'dependency-check-report/**',
                        fingerprint: true,
                        allowEmptyArchive: true
                    )
                }
            }
        }

        stage('Semgrep') {
            steps {
                sh """
                    semgrep scan \\
                        --config auto \\
                        --severity ERROR \\
                        --error \\
                        --json --output semgrep-report.json \\
                        src/
                """
            }
            post {
                always {
                    archiveArtifacts(
                        artifacts: 'semgrep-report.json',
                        fingerprint: true,
                        allowEmptyArchive: true
                    )
                }
            }
        }

        stage('Publish') {
            steps {
                sh 'dotnet publish src/BnpPoc.Api/BnpPoc.Api.csproj -c Release --no-build -o publish/'
            }
        }

        stage('Archive') {
            steps {
                sh '''
                    zip -r ${ARTIFACT_NAME} publish/
                    sha256sum ${ARTIFACT_NAME} > ${CHECKSUM_NAME}
                '''
            }
        }

        stage('Upload to Nexus') {
            steps {
                nexusUpload(file: env.ARTIFACT_NAME, repoUrl: env.NEXUS_URL)
                nexusUpload(file: env.CHECKSUM_NAME, repoUrl: env.NEXUS_URL)
            }
            post {
                always {
                    archiveArtifacts(
                        artifacts: "${ARTIFACT_NAME},${CHECKSUM_NAME}",
                        fingerprint: true,
                        allowEmptyArchive: true
                    )
                }
            }
        }

        stage('Deploy') {
            steps {
                withCredentials([
                    sshUserPrivateKey(credentialsId: 'apphost-ssh',
                        keyFileVariable: 'SSH_KEY', usernameVariable: 'SSH_USER'),
                    usernamePassword(credentialsId: 'nexus-credentials',
                        usernameVariable: 'NEXUS_USER', passwordVariable: 'NEXUS_PASS')
                ]) {
                    sh '''
                        cd deploy/ansible
                        ansible-playbook deploy.yml \
                          --private-key "$SSH_KEY" \
                          --extra-vars "artifact_version=${ARTIFACT_VERSION} \
                                        artifact_name=${ARTIFACT_NAME} \
                                        checksum_name=${CHECKSUM_NAME} \
                                        nexus_url=${NEXUS_URL} \
                                        nexus_user=$NEXUS_USER nexus_pass=$NEXUS_PASS"
                    '''
                }
            }
        }
    }

    post {
        always {
            cleanWs()
        }
    }
}
