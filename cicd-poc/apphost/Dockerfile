FROM jrei/systemd-debian:12

# python3 → Ansible modules require a Python interpreter on the managed node
# sudo    → become: true escalates the non-root deploy user to root
# unzip   → ansible.builtin.unarchive shells out to unzip/zipinfo for .zip archives
RUN apt-get update && apt-get install -y --no-install-recommends \
    openssh-server \
    curl \
    ca-certificates \
    libicu-dev \
    python3 \
    sudo \
    unzip \
    && rm -rf /var/lib/apt/lists/*

# Install ASP.NET Core 10 runtime via official script (published app needs runtime, not SDK)
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet

# Create deploy user with passwordless sudo (PoC convenience — see plan Observations)
RUN useradd -m -s /bin/bash deploy \
    && mkdir -p /home/deploy/.ssh \
    && chmod 700 /home/deploy/.ssh \
    && echo 'deploy ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/deploy \
    && chmod 0440 /etc/sudoers.d/deploy

# Bake the deploy user's authorized SSH key (public half — safe to commit)
COPY authorized_keys /home/deploy/.ssh/authorized_keys

# Key-only sshd, owned release root, enable sshd unit at boot.
# Also wires the multi-user.target.wants/ symlink for systemd-user-sessions.service:
# minimal systemd images often ship without it, so the service never runs and
# /run/nologin is never removed — SSH permanently refuses non-root logins with
# "System is booting up" even after the target is reached.
RUN chown -R deploy:deploy /home/deploy/.ssh \
    && chmod 600 /home/deploy/.ssh/authorized_keys \
    && mkdir -p /opt/cicdpoc-api \
    && chown deploy:deploy /opt/cicdpoc-api \
    && sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config \
    && systemctl enable ssh \
    && ln -sf /lib/systemd/system/systemd-user-sessions.service \
        /etc/systemd/system/multi-user.target.wants/systemd-user-sessions.service

# CMD inherited from base = /lib/systemd/systemd (PID 1)
