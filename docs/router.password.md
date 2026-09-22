# 1. Go to the config directory

`cd /mnt/jffs2`

# 2. Make a backup of the original config

`cp hw_ctree.xml hw_ctree.xml.bak`

# 3. Decrypt the config
```shell
cp hw_ctree.xml mycfg.xml.gz
aescrypt2 1 mycfg.xml.gz tem
gzip -d mycfg.xml.gz
```

# 4. Change the Telnet root password hash

> create password in https://andreluis034.github.io/huawei-utility-page/#passgen and replace the hash `261b3d123d9193059b3e5ff9d87f42ee40174f13df6f0900ab1757586d1583a6` with your new one.

```shell 
sed -i 's|Userpassword="f2eac909640cf321fb0507343fc4f8a7924a344648ece105d32cc129348e778e"|Userpassword="261b3d123d9193059b3e5ff9d87f42ee40174f13df6f0900ab1757586d1583a6"|' mycfg.xml
```

# 5. Verify the change
`grep -i "CLIUserInfoInstance" mycfg.xml`

# 6. Compress the file again

`gzip -f mycfg.xml`

# 7. Put the new config in place

`cp -f mycfg.xml.gz hw_ctree.xml`

# 8. (Optional) Make an extra backup of the new config

`cp hw_ctree.xml hw_ctree_new.xml`

# 9. Reboot

`reboot`

> If the new password does not work, you can restore the old config with:

```shell
Bashcd /mnt/jffs2
cp hw_ctree.xml.bak hw_ctree.xml
reboot
```