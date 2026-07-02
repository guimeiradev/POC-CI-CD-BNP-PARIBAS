// Reusable step: uploads a single file to a Nexus raw repository via HTTP PUT.
// Credentials are resolved at shell runtime (\${NEXUS_USER}/\${NEXUS_PASS}) — never
// interpolated at Groovy time — matching the inline pattern this step replaces.
// `file`/`repoUrl` come from controlled `env`, not external input.
def call(Map args) {
    String file          = args.file
    String repoUrl       = args.repoUrl
    String credentialsId = args.get('credentialsId', 'nexus-credentials')
    withCredentials([usernamePassword(
        credentialsId: credentialsId,
        usernameVariable: 'NEXUS_USER',
        passwordVariable: 'NEXUS_PASS'
    )]) {
        sh """
            curl -u \${NEXUS_USER}:\${NEXUS_PASS} \\
                 -X PUT "${repoUrl}/${file}" \\
                 --upload-file "${file}" \\
                 --fail --silent --show-error
        """
    }
}
