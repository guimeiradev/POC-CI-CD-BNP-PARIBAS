FROM jenkins/jenkins:lts-jdk17

USER root

# Install dependencies for dotnet-install.sh
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    libicu-dev \
    && rm -rf /var/lib/apt/lists/*

# Install .NET 10 SDK via official script
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:/usr/share/dotnet"

# Install Jenkins plugins
COPY plugins.txt /usr/share/jenkins/plugins.txt
RUN jenkins-plugin-cli --plugin-file /usr/share/jenkins/plugins.txt

USER jenkins
