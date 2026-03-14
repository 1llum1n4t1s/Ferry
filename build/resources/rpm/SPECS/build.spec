Name: ferry
Version: %_version
Release: 1
Summary: P2P File Transfer Application
License: MIT
URL: https://github.com/1llum1n4t1s/Ferry
Source: https://github.com/1llum1n4t1s/Ferry/archive/refs/tags/v%_version.tar.gz
Requires: libX11.so.6()(%{__isa_bits}bit)
Requires: libSM.so.6()(%{__isa_bits}bit)
Requires: libicu
Requires: xdg-utils

%define _build_id_links none

%description
P2P File Transfer Application

%install
mkdir -p %{buildroot}/opt/ferry
mkdir -p %{buildroot}/%{_bindir}
mkdir -p %{buildroot}/usr/share/applications
mkdir -p %{buildroot}/usr/share/icons
cp -f %{_topdir}/../../Ferry/* %{buildroot}/opt/ferry/
ln -rsf %{buildroot}/opt/ferry/ferry %{buildroot}/%{_bindir}
cp -r %{_topdir}/../_common/applications %{buildroot}/%{_datadir}
cp -r %{_topdir}/../_common/icons %{buildroot}/%{_datadir}
chmod 755 -R %{buildroot}/opt/ferry
chmod 755 %{buildroot}/%{_datadir}/applications/ferry.desktop

%files
%dir /opt/ferry/
/opt/ferry/*
/usr/share/applications/ferry.desktop
/usr/share/icons/*
%{_bindir}/ferry

%changelog
# skip
