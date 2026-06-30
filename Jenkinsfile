pipeline {
    agent any

    environment {
        DOTNET_ROOT = '/usr/share/dotnet'
        ARTIFACT_NAME = "BnpPoc.Api-${BUILD_NUMBER}.zip"
        NEXUS_URL = 'http://nexus:8081/repository/dotnet-artifacts'
    }

    stages {
        stage('Restore') {
            steps {
                sh 'dotnet restore src/BnpPoc.sln'
            }
        }

        stage('Build') {
            steps {
                sh 'dotnet build src/BnpPoc.sln --no-restore -c Release'
            }
        }

        stage('Test') {
            steps {
                sh 'dotnet test src/BnpPoc.sln --no-build -c Release'
            }
        }

        stage('Publish') {
            steps {
                sh 'dotnet publish src/BnpPoc.Api/BnpPoc.Api.csproj -c Release --no-build -o publish/'
            }
        }

        stage('Archive') {
            steps {
                sh 'zip -r ${ARTIFACT_NAME} publish/'
            }
        }

        stage('Upload to Nexus') {
            steps {
                withCredentials([usernamePassword(
                    credentialsId: 'nexus-credentials',
                    usernameVariable: 'NEXUS_USER',
                    passwordVariable: 'NEXUS_PASS'
                )]) {
                    sh '''
                        curl -u ${NEXUS_USER}:${NEXUS_PASS} \
                             -X PUT "${NEXUS_URL}/${ARTIFACT_NAME}" \
                             --upload-file "${ARTIFACT_NAME}" \
                             --fail --silent --show-error
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
