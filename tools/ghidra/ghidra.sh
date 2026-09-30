#!/bin/bash
# Uso: ghidra.sh <argomenti di analyzeHeadless...>   (JDK e Ghidra portatili in Z:\Archeage\tools)
export JAVA_HOME="Z:\Archeage\tools\jdk21\jdk-21.0.12.1+1"
export PATH="/z/Archeage/tools/jdk21/jdk-21.0.12.1+1/bin:$PATH"
cd /z/Archeage/tools/ghidra/ghidra_12.1.4_PUBLIC/support && ./analyzeHeadless.bat "$@"
