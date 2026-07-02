FROM jenkins/jenkins:lts-jdk17

USER root

# Install dependencies for dotnet-install.sh
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    libicu-dev \
    zip \
    openssh-client \
    && rm -rf /var/lib/apt/lists/*

# Install .NET 10 SDK via official script
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:/usr/share/dotnet"

# Install dotnet global tools for SonarQube scanning and coverage reporting
RUN mkdir -p /opt/dotnet-tools \
    && dotnet tool install --tool-path /opt/dotnet-tools dotnet-sonarscanner \
    && dotnet tool install --tool-path /opt/dotnet-tools dotnet-reportgenerator-globaltool
ENV PATH="${PATH}:/opt/dotnet-tools"

# Install Liquibase CLI + Microsoft JDBC driver for SQL Server migrations
ENV LIQUIBASE_VERSION=5.0.3
RUN mkdir -p /opt/liquibase \
    && curl -fsSL "https://github.com/liquibase/liquibase/releases/download/v${LIQUIBASE_VERSION}/liquibase-${LIQUIBASE_VERSION}.tar.gz" -o /tmp/liquibase.tar.gz \
    && tar -xzf /tmp/liquibase.tar.gz -C /opt/liquibase \
    && rm /tmp/liquibase.tar.gz \
    && ln -s /opt/liquibase/liquibase /usr/local/bin/liquibase

ENV MSSQL_JDBC_VERSION=13.4.0
RUN curl -fsSL "https://github.com/microsoft/mssql-jdbc/releases/download/v${MSSQL_JDBC_VERSION}/mssql-jdbc-${MSSQL_JDBC_VERSION}.jre11.jar" \
    -o /opt/liquibase/lib/mssql-jdbc-${MSSQL_JDBC_VERSION}.jre11.jar

# Install Semgrep via Python venv (avoids PEP 668 restriction on Debian Bookworm)
RUN apt-get update && apt-get install -y --no-install-recommends python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/semgrep-venv \
    && /opt/semgrep-venv/bin/pip install --quiet semgrep \
    && ln -s /opt/semgrep-venv/bin/semgrep /usr/local/bin/semgrep

# Install Ansible CLI via Python venv (mirrors the Semgrep venv; the plugin is not the binary)
RUN python3 -m venv /opt/ansible-venv \
    && /opt/ansible-venv/bin/pip install --quiet ansible-core \
    && ln -s /opt/ansible-venv/bin/ansible /usr/local/bin/ansible \
    && ln -s /opt/ansible-venv/bin/ansible-playbook /usr/local/bin/ansible-playbook

# Install Jenkins plugins
COPY plugins.txt /usr/share/jenkins/plugins.txt
RUN jenkins-plugin-cli --plugin-file /usr/share/jenkins/plugins.txt

USER jenkins
